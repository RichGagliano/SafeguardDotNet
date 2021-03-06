﻿using System;
using System.Linq;
using System.Net.Security;
using System.Security;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace OneIdentity.SafeguardDotNet.Authentication
{
    internal abstract class AuthenticatorBase : IAuthenticationMechanism
    {
        private bool _disposed;

        protected SecureString AccessToken;

        protected readonly string SafeguardRstsUrl;
        protected readonly string SafeguardCoreUrl;

        protected RestClient RstsClient;
        protected RestClient CoreClient;

        protected AuthenticatorBase(string networkAddress, int apiVersion, bool ignoreSsl, RemoteCertificateValidationCallback validationCallback)
        {
            NetworkAddress = networkAddress;
            ApiVersion = apiVersion;

            SafeguardRstsUrl = $"https://{NetworkAddress}/RSTS";
            RstsClient = new RestClient(SafeguardRstsUrl);

            SafeguardCoreUrl = $"https://{NetworkAddress}/service/core/v{ApiVersion}";
            CoreClient = new RestClient(SafeguardCoreUrl);

            if (ignoreSsl)
            {
                IgnoreSsl = true;
                ValidationCallback = null;
                RstsClient.RemoteCertificateValidationCallback += (sender, certificate, chain, errors) => true;
                CoreClient.RemoteCertificateValidationCallback += (sender, certificate, chain, errors) => true;
            } 
            else if (validationCallback != null)
            {
                IgnoreSsl = false;
                ValidationCallback = validationCallback;
                RstsClient.RemoteCertificateValidationCallback += validationCallback;
                CoreClient.RemoteCertificateValidationCallback += validationCallback;
            }
        }

        public abstract string Id { get; }

        public string NetworkAddress { get; }

        public int ApiVersion { get; }

        public bool IgnoreSsl { get; }

        public RemoteCertificateValidationCallback ValidationCallback { get; }

        public virtual bool IsAnonymous => false;

        public bool HasAccessToken()
        {
            return AccessToken != null;
        }

        public void ClearAccessToken()
        {
            AccessToken?.Dispose();
            AccessToken = null;
        }

        public SecureString GetAccessToken()
        {
            if (_disposed)
                throw new ObjectDisposedException("AuthenticatorBase");
            return AccessToken;
        }

        public int GetAccessTokenLifetimeRemaining()
        {
            if (_disposed)
                throw new ObjectDisposedException("AuthenticatorBase");
            if (!HasAccessToken())
                return 0;
            var request = new RestRequest("LoginMessage", RestSharp.Method.GET)
                .AddHeader("Accept", "application/json")
                // SecureString handling here basically negates the use of a secure string anyway, but when calling a Web API
                // I'm not sure there is anything you can do about it.
                .AddHeader("Authorization", $"Bearer {AccessToken.ToInsecureString()}")
                .AddHeader("X-TokenLifetimeRemaining", "");
            var response = CoreClient.Execute(request);
            if (response.ResponseStatus != ResponseStatus.Completed)
                throw new SafeguardDotNetException($"Unable to connect to web service {CoreClient.BaseUrl}, Error: " +
                                    response.ErrorMessage);
            if (!response.IsSuccessful)
                return 0;
            var remainingStr = response.Headers.ToList().FirstOrDefault(x => x.Name == "X-TokenLifetimeRemaining")?.Value.ToString();
            if (remainingStr == null || !int.TryParse(remainingStr, out var remaining))
                return 10; // Random magic value... the access token was good, but for some reason it didn't return the remaining lifetime
            return remaining;
        }

        public void RefreshAccessToken()
        {
            if (_disposed)
                throw new ObjectDisposedException("AuthenticatorBase");
            using (var rStsToken = GetRstsTokenInternal())
            {
                var request = new RestRequest("Token/LoginResponse", RestSharp.Method.POST)
                    .AddHeader("Accept", "application/json")
                    .AddHeader("Content-type", "application/json")
                    // SecureString handling here basically negates the use of a secure string anyway, but when calling a Web API
                    // I'm not sure there is anything you can do about it.
                    .AddJsonBody(new { StsAccessToken = rStsToken.ToInsecureString() });
                var response = CoreClient.Execute(request);
                if (response.ResponseStatus != ResponseStatus.Completed)
                    throw new SafeguardDotNetException($"Unable to connect to web service {CoreClient.BaseUrl}, Error: " +
                                                       response.ErrorMessage);
                if (!response.IsSuccessful)
                    throw new SafeguardDotNetException(
                        $"Error exchanging RSTS token from {Id} authenticator for Safeguard API access token, Error: " +
                        $"{response.StatusCode} {response.Content}", response.StatusCode, response.Content);
                var jObject = JObject.Parse(response.Content);
                AccessToken = jObject.GetValue("UserToken").ToString().ToSecureString();
            }
        }

        protected abstract SecureString GetRstsTokenInternal();

        public abstract object Clone();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed || !disposing)
                return;
            try
            {
               ClearAccessToken();
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
