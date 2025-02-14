﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Authgear.Xamarin.CsExtensions;
using Authgear.Xamarin.Data;
using Authgear.Xamarin.Data.Oauth;
using Authgear.Xamarin.DeviceInfo;
using Authgear.Xamarin.Oauth;
using Xamarin.Essentials;

namespace Authgear.Xamarin
{
    public partial class AuthgearSdk
    {
        /// <summary>
        /// To prevent user from using expired access token, we have to check in advance
        ///whether it had expired and refresh it accordingly in refreshAccessTokenIfNeeded. If we
        /// use the expiry time in OidcTokenResponse directly to check for expiry, it is possible
        /// that the access token had passed the check but ends up being expired when it arrives at
        /// the server due to slow traffic or unfair scheduler.
        ///
        /// To compat this, we should consider the access token expired earlier than the expiry time
        /// calculated using OidcTokenResponse.expiresIn. Current implementation uses
        /// ExpireInPercentage of OidcTokenResponse.expiresIn to calculate the expiry time.
        /// </summary>
        private const float ExpireInPercentage = 0.9f;
        const string LoginHintFormat = "https://authgear.com/login_hint?type=app_session_token&app_session_token={0}";
        public string ClientId
        { get; private set; }
        public SessionState SessionState
        { get; private set; }
        public string AccessToken
        { get; private set; }
        public string IdToken
        { get; private set; }
        private DateTime? expiredAt;
        private readonly string authgearEndpoint;
        private readonly bool shareSessionWithSystemBrowser;
        private readonly ITokenStorage tokenStorage;
        private readonly IContainerStorage containerStorage;
        private readonly IOauthRepo oauthRepo;
        private readonly IKeyRepo keyRepo;
        private readonly IBiometric biometric;
        private readonly IWebView webView;
        private readonly string name;
        private bool isInitialized = false;
        private string refreshToken = null;
        private Task refreshAccessTokenTask = null;
        public event EventHandler<SessionStateChangeReason> SessionStateChange;
        private bool ShouldSuppressIDPSessionCookie
        {
            get { return !shareSessionWithSystemBrowser; }
        }
        public bool CanReauthenticate
        {
            get
            {
                var idToken = IdToken;
                if (idToken == null) { return false; }
                var jsonDocument = Jwt.Decode(idToken);
                if (!jsonDocument.RootElement.TryGetProperty("https://authgear.com/claims/user/can_reauthenticate", out var can)) { return false; }
                return can.ValueKind == JsonValueKind.True;
            }
        }
        private readonly object tokenStateLock = new object();
        private AuthgearSdk(AuthgearOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (options.ClientId == null)
            {
                throw new ArgumentNullException(nameof(options.ClientId));
            }
            if (options.AuthgearEndpoint == null)
            {
                throw new ArgumentNullException(nameof(options.AuthgearEndpoint));
            }
            ClientId = options.ClientId;
            authgearEndpoint = options.AuthgearEndpoint;
            shareSessionWithSystemBrowser = options.ShareSessionWithSystemBrowser;
            tokenStorage = options.TokenStorage ?? new PersistentTokenStorage();
            name = options.Name ?? "default";
            containerStorage = new PersistentContainerStorage();
            oauthRepo = new OauthRepoHttp
            {
                Endpoint = authgearEndpoint
            };
        }

        private void EnsureIsInitialized()
        {
            if (!isInitialized)
            {
                throw new InvalidOperationException("Authgear is not configured. Did you forget to call Configure?");
            }
        }
        public async Task ConfigureAsync()
        {
            isInitialized = true;
            var refreshToken = await tokenStorage.GetRefreshTokenAsync(name);
            this.refreshToken = refreshToken;
            if (refreshToken != null)
            {
                UpdateSessionState(SessionState.Authenticated, SessionStateChangeReason.FoundToken);
            }
            else
            {
                UpdateSessionState(SessionState.NoSession, SessionStateChangeReason.NoToken);
            }
        }

        private void UpdateSessionState(SessionState state, SessionStateChangeReason reason)
        {
            SessionState = state;
            SessionStateChange?.Invoke(this, reason);
        }

        public async Task<UserInfo> AuthenticateAnonymouslyAsync()
        {
            EnsureIsInitialized();
            var challengeResponse = await oauthRepo.OauthChallengeAsync("anonymous_request");
            var challenge = challengeResponse.Token;
            var keyId = await containerStorage.GetAnonymousKeyIdAsync(name);
            var deviceInfo = PlatformGetDeviceInfo();
            var jwtResult = await keyRepo.GetOrCreateAnonymousJwtAsync(keyId, challenge, deviceInfo);
            keyId = jwtResult.KeyId;
            var jwt = jwtResult.Jwt;
            var tokenResponse = await oauthRepo.OidcTokenRequestAsync(new OidcTokenRequest
            {
                GrantType = GrantType.Anonymous,
                ClientId = ClientId,
                XDeviceInfo = GetDeviceInfoString(deviceInfo),
                Jwt = jwt
            });
            var userInfo = await oauthRepo.OidcUserInfoRequestAsync(tokenResponse.AccessToken);
            SaveToken(tokenResponse, SessionStateChangeReason.Authenciated);
            await DisableBiometricAsync();
            containerStorage.SetAnonymousKeyId(name, keyId);
            return userInfo;
        }

        /// <summary>
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="AnonymousUserNotFoundException"></exception>
        /// <exception cref="TaskCanceledException"></exception>
        public async Task<AuthenticateResult> PromoteAnonymousUserAsync(PromoteOptions options)
        {
            EnsureIsInitialized();
            var keyId = (await containerStorage.GetAnonymousKeyIdAsync(name)) ?? throw new AnonymousUserNotFoundException();
            var challengeResponse = await oauthRepo.OauthChallengeAsync("anonymous_request");
            var challenge = challengeResponse.Token;
            var jwt = await keyRepo.PromoteAnonymousUserAsync(keyId, challenge, PlatformGetDeviceInfo());
            var jwtValue = WebUtility.UrlEncode(jwt);
            var loginHint = $"https://authgear.com/login_hint?type=anonymous&jwt={jwtValue}";
            var codeVerifier = SetupVerifier();
            var authorizeUrl = await GetAuthorizeEndpointAsync(new OidcAuthenticationRequest
            {
                RedirectUri = options.RedirectUri,
                ResponseType = "code",
                Scope = new List<string>() { "openid", "offline_access", "https://authgear.com/scopes/full-access" },
                Prompt = new List<PromptOption>() { PromptOption.Login },
                LoginHint = loginHint,
                State = options.State,
                UiLocales = options.UiLocales,
                SuppressIdpSessionCookie = ShouldSuppressIDPSessionCookie,
            }, codeVerifier);
            var deepLink = await OpenAuthorizeUrlAsync(options.RedirectUri, authorizeUrl);
            var result = await FinishAuthenticationAsync(deepLink, codeVerifier.Verifier);
            containerStorage.DeleteAnonymousKeyId(name);
            return result;
        }

        /// <summary>
        /// </summary>
        /// <param name="options"></param>
        /// <exception cref="TaskCanceledException"></exception>
        /// <returns></returns>
        public async Task<AuthenticateResult> AuthenticateAsync(AuthenticateOptions options)
        {
            EnsureIsInitialized();
            var codeVerifier = SetupVerifier();
            var request = options.ToRequest(ShouldSuppressIDPSessionCookie);
            var authorizeUrl = await GetAuthorizeEndpointAsync(request, codeVerifier);
            var deepLink = await OpenAuthorizeUrlAsync(request.RedirectUri, authorizeUrl);
            return await FinishAuthenticationAsync(deepLink, codeVerifier.Verifier);
        }

        /// <summary>
        /// </summary>
        /// <param name="options"></param>
        /// <param name="biometricOptions"></param>
        /// <returns></returns>
        /// <exception cref="AuthgearException"></exception>
        /// <exception cref="TaskCanceledException"></exception>
        /// <exception cref="OperationCanceledException"></exception>
        public async Task<ReauthenticateResult> ReauthenticateAsync(ReauthenticateOptions options, BiometricOptions biometricOptions)
        {
            EnsureIsInitialized();
            if (await GetIsBiometricEnabledAsync() && biometricOptions != null)
            {
                var userInfo = await AuthenticateBiometricAsync(biometricOptions);
                return new ReauthenticateResult { State = options.State, UserInfo = userInfo };
            }
            if (!CanReauthenticate)
            {
                throw new AuthgearException("CanReauthenticate is false");
            }
            var idTokenHint = IdToken;
            if (idTokenHint == null)
            {
                throw new AuthgearException("Call refreshIdToken first");
            }
            var codeVerifier = SetupVerifier();
            var request = options.ToRequest(idTokenHint, ShouldSuppressIDPSessionCookie);
            var authorizeUrl = await GetAuthorizeEndpointAsync(request, codeVerifier);
            var deepLink = await OpenAuthorizeUrlAsync(request.RedirectUri, authorizeUrl);
            return await FinishReauthenticationAsync(deepLink, codeVerifier.Verifier);
        }

        public async Task LogoutAsync(bool? force = null)
        {
            EnsureIsInitialized();
            try
            {
                var refreshToken = await tokenStorage.GetRefreshTokenAsync(name) ?? "";
                await oauthRepo.OidcRevocationRequestAsync(refreshToken);
            }
            catch (Exception)
            {
                if (force != true)
                {
                    throw;
                }
            }
            ClearSession(SessionStateChangeReason.Logout);
        }

        public async Task OpenUrlAsync(string path)
        {
            EnsureIsInitialized();
            var refreshToken = await tokenStorage.GetRefreshTokenAsync(name);
            var appSessionTokenResponse = await oauthRepo.OauthAppSessionTokenAsync(refreshToken);
            var token = appSessionTokenResponse.AppSessionToken;
            var url = new Uri(new Uri(authgearEndpoint), path).ToString();
            var loginHint = string.Format(LoginHintFormat, WebUtility.UrlEncode(token));
            var authorizeUrl = await GetAuthorizeEndpointAsync(new OidcAuthenticationRequest
            {
                RedirectUri = url,
                ResponseType = "none",
                Scope = new List<string> { "openid", "offline_access", "https://authgear.com/scopes/full-access" },
                Prompt = new List<PromptOption>() { PromptOption.None },
                LoginHint = loginHint,
                SuppressIdpSessionCookie = ShouldSuppressIDPSessionCookie,
            }, null);
            await webView.ShowAsync(authorizeUrl);
        }

        public async Task OpenAsync(SettingsPage page)
        {
            await OpenUrlAsync(page.GetDescription());
        }

        private VerifierHolder SetupVerifier()
        {
            var verifier = GenerateCodeVerifier();
            return new VerifierHolder { Verifier = verifier, Challenge = ComputeCodeChallenge(verifier) };
        }

        private string GenerateCodeVerifier()
        {
            const int byteCount = 32;
            var bytes = new Byte[byteCount];
            using (var provider = new RNGCryptoServiceProvider())
            {
                provider.GetBytes(bytes);
                return string.Join("", bytes.Select(x => x.ToString("x2")));
            }
        }

        private string ComputeCodeChallenge(string verifier)
        {
            var hash = Sha256(verifier);
            return ConvertExtensions.ToBase64UrlSafeString(hash);
        }

        private byte[] Sha256(string input)
        {
            var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        }

        private bool ShouldRefreshAccessToken
        {
            get
            {
                if (refreshToken == null) return false;
                if (AccessToken == null) return true;
                var expireAt = this.expiredAt;
                if (expiredAt == null) return true;
                var now = DateTime.Now;
                if (expireAt < now) return true;
                return false;
            }
        }

        private async Task RefreshAccessTokenAsync()
        {
            var taskSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existingTask = Interlocked.CompareExchange(ref refreshAccessTokenTask, taskSource.Task, null);
            var isExchanged = existingTask == null;
            if (isExchanged)
            {
                try
                {
                    await DoRefreshAccessTokenAsync();
                    taskSource.SetResult(null);
                }
                catch (Exception ex)
                {
                    taskSource.SetException(ex);
                    throw;
                }
                finally
                {
                    refreshAccessTokenTask = null;
                }
            }
            else
            {
                // Shouldn't need to, just in case.
                taskSource.SetCanceled();
                await existingTask;
            }
        }

        private async Task DoRefreshAccessTokenAsync()
        {
            var refreshToken = await tokenStorage.GetRefreshTokenAsync(name);
            if (refreshToken == null)
            {
                // Somehow we are asked to refresh access token but we don't have the refresh token.
                // Something went wrong, clear session.
                ClearSession(SessionStateChangeReason.NoToken);
                return;
            }
            try
            {
                var tokenResponse = await oauthRepo.OidcTokenRequestAsync(
                    new OidcTokenRequest
                    {
                        GrantType = GrantType.RefreshToken,
                        ClientId = ClientId,
                        XDeviceInfo = GetDeviceInfoString(),
                        RefreshToken = refreshToken
                    });
                SaveToken(tokenResponse, SessionStateChangeReason.FoundToken);
            }
            catch (Exception ex)
            {
                if (ex is OauthException)
                {
                    var oauthEx = ex as OauthException;
                    if (oauthEx.Error == "invalid_grant")
                    {
                        ClearSession(SessionStateChangeReason.Invalid);
                        return;
                    }
                }
                throw;
            }
        }

        private async Task<string> GetAuthorizeEndpointAsync(OidcAuthenticationRequest request, VerifierHolder codeVerifier)
        {
            var config = await oauthRepo.GetOidcConfigurationAsync();
            var query = request.ToQuery(ClientId, codeVerifier);
            return $"{config.AuthorizationEndpoint}?{query.ToQueryParameter()}";
        }

        /// <summary>
        /// </summary>
        /// <param name="redirectUrl"></param>
        /// <param name="authorizeUrl"></param>
        /// <exception cref="TaskCanceledException"></exception>
        /// <returns>Redirect URI with query parameters</returns>
        private async Task<string> OpenAuthorizeUrlAsync(string redirectUrl, string authorizeUrl)
        {
            // WebAuthenticator abstracts the uri for us but we need the uri in FinishAuthorization.
            // Substitute the uri for now.
            var result = await WebAuthenticator.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = new Uri(authorizeUrl),
                CallbackUrl = new Uri(redirectUrl),
                PrefersEphemeralWebBrowserSession = !shareSessionWithSystemBrowser
            });
            var builder = new UriBuilder(redirectUrl)
            {
                Query = result.Properties.ToQueryParameter()
            };
            return builder.ToString();
        }

        private async Task<(UserInfo userInfo, OidcTokenResponse tokenResponse, string state)> ParseDeepLinkAndGetUserAsync(string deepLink, string codeVerifier)
        {
            var uri = new Uri(deepLink);
            var path = uri.LocalPath == "/" ? "" : uri.LocalPath;
            var redirectUri = $"{uri.Scheme}://{uri.Authority}{path}";
            var query = uri.ParseQueryString();
            query.TryGetValue("state", out var state);
            query.TryGetValue("error", out var error);
            query.TryGetValue("error_description", out var errorDescription);
            query.TryGetValue("error_uri", out var errorUri);
            if (error != null)
            {
                throw new OauthException(error, errorDescription, state, errorUri);
            }
            query.TryGetValue("code", out var code);
            if (code == null)
            {
                throw new OauthException("invalid_request", "Missing parameter: code", state, errorUri);
            }
            var tokenResponse = await oauthRepo.OidcTokenRequestAsync(new OidcTokenRequest
            {
                GrantType = GrantType.AuthorizationCode,
                ClientId = ClientId,
                XDeviceInfo = GetDeviceInfoString(),
                Code = code,
                RedirectUri = redirectUri,
                CodeVerifier = codeVerifier ?? "",
            });
            var userInfo = await oauthRepo.OidcUserInfoRequestAsync(tokenResponse.AccessToken);
            return (userInfo, tokenResponse, state);
        }

        private async Task<AuthenticateResult> FinishAuthenticationAsync(string deepLink, string codeVerifier)
        {
            (var userInfo, var tokenResponse, var state) = await ParseDeepLinkAndGetUserAsync(deepLink, codeVerifier);
            SaveToken(tokenResponse, SessionStateChangeReason.Authenciated);
            await DisableBiometricAsync();
            return new AuthenticateResult { UserInfo = userInfo, State = state };
        }

        private async Task<ReauthenticateResult> FinishReauthenticationAsync(string deepLink, string codeVerifier)
        {
            (var userInfo, var tokenResponse, var state) = await ParseDeepLinkAndGetUserAsync(deepLink, codeVerifier);
            if (tokenResponse.IdToken != null)
            {
                IdToken = tokenResponse.IdToken;
            }
            return new ReauthenticateResult { UserInfo = userInfo, State = state };
        }

        public void EnsureBiometricIsSupported(BiometricOptions options)
        {
            EnsureIsInitialized();
            biometric.EnsureIsSupported(options);
        }

        public async Task<bool> GetIsBiometricEnabledAsync()
        {
            EnsureIsInitialized();
            var kid = await containerStorage.GetBiometricKeyIdAsync(name);
            if (kid == null) { return false; }
            return true;
        }

        public async Task DisableBiometricAsync()
        {
            EnsureIsInitialized();
            var kid = await containerStorage.GetBiometricKeyIdAsync(name);
            if (kid != null)
            {
                biometric.RemoveBiometric(kid);
                containerStorage.DeleteBiometricKeyId(name);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="UnauthenticatedUserException"></exception>
        /// <exception cref="OperationCanceledException"></exception>
        public async Task EnableBiometricAsync(BiometricOptions options)
        {
            EnsureIsInitialized();
            await RefreshAccessTokenIfNeededAsync();
            var accessToken = AccessToken ?? throw new UnauthenticatedUserException();
            var challengeResponse = await oauthRepo.OauthChallengeAsync("biometric_request");
            var challenge = challengeResponse.Token;
            var result = await biometric.EnableBiometricAsync(options, challenge, PlatformGetDeviceInfo());
            await oauthRepo.BiometricSetupRequestAsync(accessToken, ClientId, result.Jwt);
            containerStorage.SetBiometricKeyId(name, result.Kid);
        }

        /// <summary>
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="BiometricPrivateKeyNotFoundException"></exception>
        /// <exception cref="OperationCanceledException"></exception>
        public async Task<UserInfo> AuthenticateBiometricAsync(BiometricOptions options)
        {
            EnsureIsInitialized();
            var kid = await containerStorage.GetBiometricKeyIdAsync(name) ?? throw new BiometricPrivateKeyNotFoundException();
            var challengeResponse = await oauthRepo.OauthChallengeAsync("biometric_request");
            var challenge = challengeResponse.Token;
            try
            {
                var deviceInfo = PlatformGetDeviceInfo();
                var jwt = await biometric.AuthenticateBiometricAsync(options, kid, challenge, deviceInfo);
                try
                {
                    var tokenResponse = await oauthRepo.OidcTokenRequestAsync(new OidcTokenRequest
                    {
                        GrantType = GrantType.Biometric,
                        ClientId = ClientId,
                        XDeviceInfo = GetDeviceInfoString(deviceInfo),
                        Jwt = jwt
                    });
                    var userInfo = await oauthRepo.OidcUserInfoRequestAsync(tokenResponse.AccessToken);
                    SaveToken(tokenResponse, SessionStateChangeReason.Authenciated);
                    return userInfo;
                }
                catch (OauthException ex)
                {
                    // In case the biometric was removed remotely.
                    if (ex.Error == "invalid_grant" && ex.ErrorDescription == "InvalidCredentials")
                    {
                        await DisableBiometricAsync();
                    }
                    throw;
                }
            }
            catch (BiometricPrivateKeyNotFoundException)
            {
                await DisableBiometricAsync();
                throw;
            }
            catch (Exception ex)
            {
                throw AuthgearException.Wrap(ex);
            }
        }

        private string GetDeviceInfoString()
        {
            return ConvertExtensions.ToBase64UrlSafeString(JsonSerializer.Serialize(PlatformGetDeviceInfo()), Encoding.UTF8);
        }

        private string GetDeviceInfoString(DeviceInfoRoot deviceInfo)
        {
            return ConvertExtensions.ToBase64UrlSafeString(JsonSerializer.Serialize(deviceInfo), Encoding.UTF8);
        }

        private void SaveToken(OidcTokenResponse tokenResponse, SessionStateChangeReason reason)
        {
            if (tokenResponse == null)
            {
                throw new ArgumentNullException(nameof(tokenResponse));
            }
            lock (tokenStateLock)
            {
                if (tokenResponse.AccessToken != null)
                {
                    AccessToken = tokenResponse.AccessToken;
                }
                if (tokenResponse.RefreshToken != null)
                {
                    refreshToken = tokenResponse.RefreshToken;
                }
                if (tokenResponse.IdToken != null)
                {
                    IdToken = tokenResponse.IdToken;
                }
                if (tokenResponse.ExpiresIn != null)
                {
                    expiredAt = DateTime.Now.AddMilliseconds(((float)tokenResponse.ExpiresIn * ExpireInPercentage));
                }
                UpdateSessionState(SessionState.Authenticated, reason);
            }
            if (tokenResponse.RefreshToken != null)
            {
                tokenStorage.SetRefreshToken(name, tokenResponse.RefreshToken);
            }
        }

        public async Task<UserInfo> FetchUserInfoAsync()
        {
            EnsureIsInitialized();
            await RefreshAccessTokenIfNeededAsync();
            var accessToken = AccessToken ?? throw new UnauthenticatedUserException();
            return await oauthRepo.OidcUserInfoRequestAsync(accessToken);
        }

        public async Task RefreshIdTokenAsync()
        {
            EnsureIsInitialized();
            await RefreshAccessTokenIfNeededAsync();
            var accessToken = AccessToken ?? throw new UnauthenticatedUserException();
            var tokenResponse = await oauthRepo.OidcTokenRequestAsync(new OidcTokenRequest
            {
                GrantType = GrantType.IdToken,
                ClientId = ClientId,
                XDeviceInfo = GetDeviceInfoString(),
                AccessToken = accessToken,
            });
            if (tokenResponse.IdToken != null)
            {
                IdToken = tokenResponse.IdToken;
            }
        }

        public async Task<string> RefreshAccessTokenIfNeededAsync()
        {
            EnsureIsInitialized();
            if (ShouldRefreshAccessToken)
            {
                await RefreshAccessTokenAsync();
            }
            return AccessToken;
        }

        public void ClearSession(SessionStateChangeReason reason)
        {
            tokenStorage.DeleteRefreshToken(name);
            lock (tokenStateLock)
            {
                AccessToken = null;
                refreshToken = null;
                IdToken = null;
                expiredAt = null;
            }
            UpdateSessionState(SessionState.NoSession, reason);
        }
    }
}
