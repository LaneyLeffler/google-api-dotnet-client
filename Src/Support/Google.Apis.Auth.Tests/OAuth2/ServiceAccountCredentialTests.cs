﻿/*
Copyright 2017 Google LLC

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    https://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Google.Apis.Json;
using Google.Apis.Tests.Mocks;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Xunit;
using static Google.Apis.Auth.JsonWebSignature;

namespace Google.Apis.Auth.Tests.OAuth2
{
    public class ServiceAccountCredentialTests
    {
        private static readonly Assembly CurrentAssembly = typeof(ServiceAccountCredentialTests).Assembly;
        private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(60);
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task ValidLocallySignedAccessToken_FromPrivateKey()
        {
            string dummyServiceAccountCredentialFileContents = GetContents("service_account_credential.json");

            var credentialParameters = NewtonsoftJsonSerializer.Instance.Deserialize<JsonCredentialParameters>(dummyServiceAccountCredentialFileContents);
            var initializer = new ServiceAccountCredential.Initializer(credentialParameters.ClientEmail)
            {
                Clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            };
            var cred = new ServiceAccountCredential(initializer.FromPrivateKey(credentialParameters.PrivateKey));

            Assert.False(cred.Scopes?.Any()); // HasScopes must be false for the type of access token we want to test.

            string accessToken = await cred.GetAccessTokenForRequestAsync("http://authurl/");

            string expectedToken =
                "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJDTElFTlRfRU1BSUwiLCJz" +
                "dWIiOiJDTElFTlRfRU1BSUwiLCJhdWQiOiJodHRwOi8vYXV0aHVybC8iLCJleHAiOjE0N" +
                "TE2MTAwMDAsImlhdCI6MTQ1MTYwNjQwMH0.WLljSaAqxMVZnAxFA2SvpA3n2WRlQW71Nb" +
                "CUkbN-ZI-EWoL-HhgiV_3ISrXMvbDHYhBR0vvtXE0PcRcsMEf51Y0jV4DXZ8hf-QJFq7O" +
                "Hrepwe93dnDE6uNVnbj41_0phuy1WKwae29Qp2aPI2Y8E8Z2tXQlF87E_MdgjXVeTF8k";
            Assert.Equal(expectedToken, accessToken);
        }

        [Fact]
        public async Task ValidLocallySignedAccessToken_FromPrivateKey_AlsoOnRetry()
        {
            string dummyServiceAccountCredentialFileContents = GetContents("service_account_credential.json");

            var credentialParameters = NewtonsoftJsonSerializer.Instance.Deserialize<JsonCredentialParameters>(dummyServiceAccountCredentialFileContents);
            var messageHandler = new FetchesTokenMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer(credentialParameters.ClientEmail)
            {
                Clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                // This fetches the token "a" from server side.
                HttpClientFactory = new MockHttpClientFactory(new FetchesTokenMessageHandler()),
            };
            var cred = new ServiceAccountCredential(initializer.FromPrivateKey(credentialParameters.PrivateKey));
            
            // We obtain an access token normally.
            // This should be a self signed token not a server side token.
            string accessToken = await cred.GetAccessTokenForRequestAsync("http://authurl/");
            Assert.NotNull(accessToken);
            Assert.NotEmpty(accessToken);
            Assert.NotEqual("a", accessToken);
            Assert.Equal(0, messageHandler.Calls);
            // If the token was fetched from the service, the TokenResponse would have been set.
            Assert.Null(cred.Token);

            var fakeFailedRequest = new HttpRequestMessage();
            fakeFailedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            // On retries (i.e. when handling unsuccesful requests), we shouldn't need to
            // attempt to fetch a server side token if the credential is using self signed JWTs.
            // Instead a JWT will be regenerated, if needed, next time the request is attempted.
            bool handled = await cred.HandleResponseAsync(new HandleUnsuccessfulResponseArgs
            {
                CurrentFailedTry = 1,
                TotalTries = 3,
                Request = fakeFailedRequest,
                Response = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            });
            Assert.True(handled);
            Assert.Equal(0, messageHandler.Calls);
            // If the token was fetched from the service, the TokenResponse would have been set.
            Assert.Null(cred.Token);
        }

        private string ToHex(byte[] bs) => bs.Aggregate("", (acc, b) => acc + b.ToString("X2"));

        [Fact]
        public void Pkcs8Decoding_FromPrivateKey()
        {
            string dummyServiceAccountCredentialFileContents = GetContents("service_account_credential.json");

            var credentialParameters = NewtonsoftJsonSerializer.Instance.Deserialize<JsonCredentialParameters>(dummyServiceAccountCredentialFileContents);
            var initializer = new ServiceAccountCredential.Initializer(credentialParameters.ClientEmail)
            {
                Clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            };
            initializer.FromPrivateKey(credentialParameters.PrivateKey);
            var ps = initializer.Key.ExportParameters(true);

            Assert.Equal("924CE874F8B3A6ED3AC7DEDB1E33AF3AF0494B66B51DFB5D34E2D2C3C04530C5F2ECE65F2F149C4E51715273107E53D92B33FF2957E1297F088E0B9E8D65F23F4688725BE3D2D246359EE18C7DDFAAB45E3F47D11D474F1BE350FAB002C4F0CB67CA053A20FAAD5FAEA73DA3CB36AFA2D88AF5060D56E1B94CC268C26C7FEC77", ToHex(ps.Modulus));
            Assert.Equal("010001", ToHex(ps.Exponent));
            Assert.Equal("8B41535ECBBFCD1B3001419A5624221E3ACBB94EB90521D73558D5FF67CB3442A71961AAA658BAF33D485D8F92DA7C1B51A93BAE71ACABDFF417A8EDB16FA165B2E429750EBAE131A5E0B929DEA78165FF7F68EA9A195469B8908A353D71593BA4CABC107911B6F968B273135001337A6A6E4143F09F53D91E1989901BD9E251", ToHex(ps.D));
            Assert.Equal("C212BAF197E7B7070503712D3254412608D486EB1757B36F3BF1BF2BCDB7BBDCC9A2E92BAE38B8B73D4F7627E8FE7FF30E76D62B935DE610375899DE5D86BB79", ToHex(ps.P));
            Assert.Equal("C0FBC312C0D3FD6E32E21E6782FD5AC06040E5A2C513524D58CFC6C27E03D5C1A91031CA15111A06DC4C0C2012A4726EC3CAFBCD4CB0A4A86605971F192DFB6F", ToHex(ps.Q));
            Assert.Equal("35D48029F6DA7CB7E3BA1AB0509F721A9CA4666FDADFA69399EAE9FDBA67D621DD83E46D0B3C0C7036FF4D64B089B6EFB1F9605A61DBCFAE7BCB85925A1ABEF1", ToHex(ps.DP));
            Assert.Equal("5ABA4234DFD90A4DB3B860E8F3495F50103092855AB7C1BAC16535A19C92FAFCC819E7FE84B6FC907B237993DE8FD788C19DFD91C05B4F9E2810BAC29118F01B", ToHex(ps.DQ));
            Assert.Equal("43C57603B953654A7C02C6D5A85EAB6CB8A251F24CA366ED19C950BB80C45724BECA6140EB9B570CB52E47D103A8961DA8735927907CE19E69DA9AE48D64CB70", ToHex(ps.InverseQ));
        }

        [Fact]
        public async Task ValidLocallySignedAccessToken_FromX509Certificate()
        {
#if NETCOREAPP3_1 || NET461 || NET6_0
            const string sPfx = @"
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";

            var x509Cert = new X509Certificate2(Convert.FromBase64String(sPfx));
#elif NET452 || NET46
            const string sPrivateKey = @"
-----BEGIN PRIVATE KEY-----
MIICdgIBADANBgkqhkiG9w0BAQEFAASCAmAwggJcAgEAAoGBALQoMIfUT93VMvb5
4U5dsJLXD1D6z4o/VuVlNUaf5xH01qAv8Egpxe6f5frLB/p1eNyDjNIZ3QitD6aN
9Vfh4ifg6txBuBJtIMfSDvRJITNZ2SjKJREbflZuBk0HINKYQk2H+3TIzUbE/pN4
Cu6Mids5L/oVOnRWIe3bEC+PHR7ZAgMBAAECgYBI1qrwb+2ukeFeK59lcMnQRLVD
l3RLv9ohOy80E7h38RbJgzhR5NnK5ck1AduC7vXjqihIVf6g4F+ghmq4knI94KPQ
2fOyQGVV6jVeRVqMusx7tP9V1H26yABiK6TPklcCsWwADFmexfOxfIgbBGSmlbAd
N2/ad1Xog1xDebrbEQJBAO+2qSIDX1ahfxUyvvcMaix6Mrh/OYKb5aH1Y/7MfAav
lpbQavQHNvak2147aPNxy0ZvWdJ2HVA7hUMkgysYWSUCQQDAZalkZMSrve+Onh55
U+w5xjLklhh8PhYxx8plT8ae9VG75dGvWSz/W81B3ILg2lrNU6o08NKBJdZsJYmc
BeKlAkBtJh3zF9gEaTqlW1rqwKNjpyyLJ5r3JqczzLmAXnmmzbLi7vmULejP+5bL
XH/YQZtOcgtTMmb8jm2Kegijyc1lAkANGt+e5v4+dIGMxVhuCzlb9hQhXdftHo2E
dodivzxYN32Jvu25c+mMu0QP6GVBy53Dvp8pW/36rgkc9LGa3wvBAkEA7dGAl95T
UeNFVfuzkYNtQppcSgrx1oTcpTHoNgcvk8AgBf4yDdJJyo0IUONmgJCVffc1aTWn
nf/8cW9YC+0icQ==
-----END PRIVATE KEY-----";
            const string sCert = @"
MIICNDCCAZ2gAwIBAgIJAKl3qU1+NsuuMA0GCSqGSIb3DQEBCwUAMDMxCzAJBgNV
BAYTAkdCMRMwEQYDVQQIDApTb21lLVN0YXRlMQ8wDQYDVQQKDAZHb29nbGUwHhcN
MTYwODEwMTQwOTQ2WhcNNDMxMjI3MTQwOTQ2WjAzMQswCQYDVQQGEwJHQjETMBEG
A1UECAwKU29tZS1TdGF0ZTEPMA0GA1UECgwGR29vZ2xlMIGfMA0GCSqGSIb3DQEB
AQUAA4GNADCBiQKBgQC0KDCH1E/d1TL2+eFOXbCS1w9Q+s+KP1blZTVGn+cR9Nag
L/BIKcXun+X6ywf6dXjcg4zSGd0IrQ+mjfVX4eIn4OrcQbgSbSDH0g70SSEzWdko
yiURG35WbgZNByDSmEJNh/t0yM1GxP6TeArujInbOS/6FTp0ViHt2xAvjx0e2QID
AQABo1AwTjAdBgNVHQ4EFgQUMqzJi099PA8ML5CV1OSiHgiTGoUwHwYDVR0jBBgw
FoAUMqzJi099PA8ML5CV1OSiHgiTGoUwDAYDVR0TBAUwAwEB/zANBgkqhkiG9w0B
AQsFAAOBgQBQ9cMInb2rEcg8TTYq8MjDEegHWLUI9Dq/IvP/FHyKDczza4eX8m+G
3buutN74pX2/GHRgqvqEvvUUuAQUnZ36k6KjTNxfzNLiSXDPNeYmy6PWsUZy4Rye
/Van/ePiXdipTKMiUyl7V6dTjkE5p/e372wNVXUpcxOMmYdWmzSMvg==";

            var x509Cert = new X509Certificate2(Convert.FromBase64String(sCert));
            RSAParameters rsaParameters = Pkcs8.DecodeRsaParameters(sPrivateKey);
            var privateKey = new System.Security.Cryptography.RSACryptoServiceProvider();
            privateKey.ImportParameters(rsaParameters);
            x509Cert.PrivateKey = privateKey;
#else
#error Unsupported target
#endif
            Assert.True(x509Cert.HasPrivateKey);

            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                Clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            };
            var cred = new ServiceAccountCredential(initializer.FromCertificate(x509Cert));

            Assert.False(cred.Scopes?.Any()); // HasScopes must be false for the type of access token we want to test.

            string accessToken = await cred.GetAccessTokenForRequestAsync("http://authurl/");

            string expectedToken =
                "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzb21lLWlkIiwic3ViIjoi" +
                "c29tZS1pZCIsImF1ZCI6Imh0dHA6Ly9hdXRodXJsLyIsImV4cCI6MTQ1MTYxMDAwMCwia" +
                "WF0IjoxNDUxNjA2NDAwfQ.GfpDHgrFi4ZlGC5LuJEarLU4_eTrT5PVa-S40YtkdB2E1f3" +
                "4naYG2ItcfBEFg7Gbdkr1cIAyipuhEd2yLfPmWGwhOwVcBRNyK_J5w8RodS44mxNJwau0" +
                "jKy4x1K20ybLqcnNgzE0wag6fi5GHwdNIB0URdHDTiC88CRYdl1CIdk";
            Assert.Equal(expectedToken, accessToken);
        }

        internal const string PrivateKey = @"-----BEGIN PRIVATE KEY-----
MIICdgIBADANBgkqhkiG9w0BAQEFAASCAmAwggJcAgEAAoGBAJJM6HT4s6btOsfe
2x4zrzrwSUtmtR37XTTi0sPARTDF8uzmXy8UnE5RcVJzEH5T2Ssz/ylX4Sl/CI4L
no1l8j9GiHJb49LSRjWe4Yx936q0Xj9H0R1HTxvjUPqwAsTwy2fKBTog+q1frqc9
o8s2r6LYivUGDVbhuUzCaMJsf+x3AgMBAAECgYEAi0FTXsu/zRswAUGaViQiHjrL
uU65BSHXNVjV/2fLNEKnGWGqpli68z1IXY+S2nwbUak7rnGsq9/0F6jtsW+hZbLk
KXUOuuExpeC5Kd6ngWX/f2jqmhlUabiQijU9cVk7pMq8EHkRtvlosnMTUAEzempu
QUPwn1PZHhmJkBvZ4lECQQDCErrxl+e3BwUDcS0yVEEmCNSG6xdXs2878b8rzbe7
3Mmi6SuuOLi3PU92J+j+f/MOdtYrk13mEDdYmd5dhrt5AkEAwPvDEsDT/W4y4h5n
gv1awGBA5aLFE1JNWM/Gwn4D1cGpEDHKFREaBtxMDCASpHJuw8r7zUywpKhmBZcf
GS37bwJANdSAKfbafLfjuhqwUJ9yGpykZm/a36aTmerp/bpn1iHdg+RtCzwMcDb/
TWSwibbvsflgWmHbz657y4WSWhq+8QJAWrpCNN/ZCk2zuGDo80lfUBAwkoVat8G6
wWU1oZyS+vzIGef+hLb8kHsjeZPej9eIwZ39kcBbT54oELrCkRjwGwJAQ8V2A7lT
ZUp8AsbVqF6rbLiiUfJMo2btGclQu4DEVyS+ymFA65tXDLUuR9EDqJYdqHNZJ5B8
4Z5p2prkjWTLcA==
-----END PRIVATE KEY-----";

        [Fact]
        public async Task JwtCache_Size()
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                Clock = clock
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);
            Assert.False(cred.HasExplicitScopes); // Must be false for the remainder of this test to be valid.
            Assert.False(cred.UseJwtAccessWithScopes);
            // Check JWTs removed from cache once cache fills up.
            var jwt0 = await cred.GetAccessTokenForRequestAsync("uri0");
            for (int i = 0; i < ServiceAccountCredential.JwtCacheMaxSize; i++)
            {
                await cred.GetAccessTokenForRequestAsync($"uri{i}");
                // Check jwt is retrieved from cache.
                var jwt0Cached = await cred.GetAccessTokenForRequestAsync("uri0");
                Assert.Same(jwt0, jwt0Cached);
            }
            // Add one more JWT to cache that should remove jwt0 from the cache.
            await cred.GetAccessTokenForRequestAsync("uri_too_much");
            var jwt0Uncached = await cred.GetAccessTokenForRequestAsync("uri0");
            Assert.NotSame(jwt0, jwt0Uncached);
        }

        [Fact]
        public async Task JwtCache_Scoped_CachesOne()
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                UseJwtAccessWithScopes = true,
                Scopes = new[] { "scope1", "scope2" },
                Clock = clock
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);
            Assert.True(cred.HasExplicitScopes); // Must be true for the remainder of this test to be valid.
            Assert.True(cred.UseJwtAccessWithScopes);
            // Check that only one JWT is cached
            var jwt0 = await cred.GetAccessTokenForRequestAsync("uri0");
            for (int i = 0; i < ServiceAccountCredential.JwtCacheMaxSize; i++)
            {
                await cred.GetAccessTokenForRequestAsync($"uri{i}");
                // Check jwt is retrieved from cache.
                var jwt0Cached = await cred.GetAccessTokenForRequestAsync("uri0");
                Assert.Same(jwt0, jwt0Cached);
            }
            // Add one more JWT to cache that should remove jwt0 from the cache.
            await cred.GetAccessTokenForRequestAsync("uri_too_much");
            var jwt0Uncached = await cred.GetAccessTokenForRequestAsync("uri0");
            //  Scoped credential cache just one token regardless of authUri.
            Assert.Same(jwt0, jwt0Uncached);
        }

        [Fact]
        public async Task JwtCache_Expiry()
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                Clock = clock
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);
            Assert.False(cred.HasExplicitScopes); // Must be false for the remainder of this test to be valid.
            // Check JWTs are only cached until the expiry minus the expiry window.
            var jwt0 = await cred.GetAccessTokenForRequestAsync("uri");
            clock.UtcNow += ServiceAccountCredential.JwtLifetime - ServiceAccountCredential.JwtCacheExpiryWindow - TimeSpan.FromSeconds(1);
            var jwt0Cached = await cred.GetAccessTokenForRequestAsync("uri");
            Assert.Same(jwt0, jwt0Cached);
            clock.UtcNow += TimeSpan.FromSeconds(2);
            var jwt0Uncached = await cred.GetAccessTokenForRequestAsync("uri");
            Assert.NotSame(jwt0, jwt0Uncached);
        }

        [Theory]
        [CombinatorialData]
        public async Task JwtScopes_UseAud(bool useJwtAccessWithScopes)
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                UseJwtAccessWithScopes = useJwtAccessWithScopes,
                Clock = clock
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);
            Assert.False(cred.HasExplicitScopes); // Must be false for the remainder of this test to be valid.

            var jwt0 = await cred.GetAccessTokenForRequestAsync("uri0");
            byte[] decoded = TokenEncodingHelpers.Base64UrlDecode(jwt0.Split(new char[] { '.' })[1]);
            string payload = System.Text.Encoding.UTF8.GetString(decoded);

            Assert.Contains("\"aud\":\"uri0\"", payload);
            Assert.DoesNotContain("scope", payload);
        }

        [Fact]
        public async Task JwtScopes_UseScopes()
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                UseJwtAccessWithScopes = true,
                Scopes = new[] { "scope1", "scope2" },
                Clock = clock
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);
            Assert.True(cred.HasExplicitScopes); // Must be true for the remainder of this test to be valid.

            var jwt0 = await cred.GetAccessTokenForRequestAsync("uri0");
            byte[] decoded = TokenEncodingHelpers.Base64UrlDecode(jwt0.Split(new char[] { '.' })[1]);
            string payload = System.Text.Encoding.UTF8.GetString(decoded);
            Assert.Contains("\"scope\":\"scope1 scope2\"", payload);
            Assert.DoesNotContain("aud", payload);
        }

        [Theory]
        [CombinatorialData]
        public async Task Jwt_WithUser_AccessToken(bool useJwtAccessWithScopes)
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var handler = new FetchesTokenMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                UseJwtAccessWithScopes = useJwtAccessWithScopes,
                Clock = clock,
                User = "user1",
                HttpClientFactory = new MockHttpClientFactory(handler)
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);
            Assert.False(cred.HasExplicitScopes); // Must be false for the remainder of this test to be valid.

            var token = await cred.GetAccessTokenForRequestAsync("uri0");
            Assert.Equal("a", token);
        }

        [Fact]
        public async Task Jwt_WithUserAndScopes_AccessToken()
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var handler = new FetchesTokenMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                UseJwtAccessWithScopes = true,
                Scopes = new[] { "scope1", "scope2" },
                Clock = clock,
                User = "user1",
                HttpClientFactory = new MockHttpClientFactory(handler)
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);
            Assert.True(cred.HasExplicitScopes); // Must be true for the remainder of this test to be valid.

            var token = await cred.GetAccessTokenForRequestAsync("uri0");
            Assert.Equal("a", token);
        }

        [Fact]
        public async Task AccessToken_ExceptionRetry_NoDefaultRetry()
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var handler = new ResponseExceptionOnFetchingTokenMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                // Let's force access token fetching
                UseJwtAccessWithScopes = false,
                Scopes = new[] { "scope1", "scope2" },
                Clock = clock,
                HttpClientFactory = new MockHttpClientFactory(handler)
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);

            await Assert.ThrowsAsync<HttpRequestException>(() => cred.GetAccessTokenForRequestAsync("uri0"));
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task AccessToken_ExceptionRetry_ConfiguredRetry()
        {
            var clock = new MockClock(new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var handler = new ResponseExceptionOnFetchingTokenMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer("some-id")
            {
                // Let's force access token fetching
                UseJwtAccessWithScopes = false,
                Scopes = new[] { "scope1", "scope2" },
                Clock = clock,
                HttpClientFactory = new MockHttpClientFactory(handler),
                DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.Exception | ExponentialBackOffPolicy.UnsuccessfulResponse503
            }.FromPrivateKey(PrivateKey);
            var cred = new ServiceAccountCredential(initializer);

            await Assert.ThrowsAsync<HttpRequestException>(() => cred.GetAccessTokenForRequestAsync("uri0"));
            // Exceptions are retried 3 times by default.
            Assert.Equal(3, handler.Calls);
        }

        private class DummyAccessMethod : IAccessMethod
        {
            public string GetAccessToken(HttpRequestMessage request) => throw new NotImplementedException();
            public void Intercept(HttpRequestMessage request, string accessToken) => throw new NotImplementedException();
        }

        private class DummyHttpClientFactory : IHttpClientFactory
        {
            public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => null;
        }

        [Fact]
        public void CreateWithScopes()
        {
            var serviceAccountCred = new ServiceAccountCredential(new ServiceAccountCredential.Initializer("MyId", "MyTokenServerUrl")
            {
                Clock = new MockClock(DateTime.UtcNow),
                AccessMethod = new DummyAccessMethod(),
                HttpClientFactory = new DummyHttpClientFactory(),
                DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.Exception, // This is not the default
                ProjectId = "a_project_id",
                User = "a_user",
                Scopes = new[] { "scope1" },
            }.FromPrivateKey(PrivateKey));
            var cred1 = GoogleCredential.FromServiceAccountCredential(serviceAccountCred);
            var cred2 = cred1.CreateScoped("scope2");

            var svc1 = (ServiceAccountCredential)cred1.UnderlyingCredential;
            var svc2 = (ServiceAccountCredential)cred2.UnderlyingCredential;

            Assert.Same(serviceAccountCred, svc1);
            Assert.NotSame(serviceAccountCred, svc2);

            Assert.Same(svc1.Id, svc2.Id);
            Assert.Same(svc1.TokenServerUrl, svc2.TokenServerUrl);
            Assert.Same(svc1.Clock, svc2.Clock);
            Assert.Same(svc1.AccessMethod, svc2.AccessMethod);
            Assert.Same(svc1.HttpClientFactory, svc2.HttpClientFactory);
            Assert.Equal(svc1.DefaultExponentialBackOffPolicy, svc2.DefaultExponentialBackOffPolicy);
            Assert.Same(svc1.ProjectId, svc2.ProjectId);
            Assert.Same(svc1.User, svc2.User);
            Assert.NotSame(svc1.Scopes, svc2.Scopes);
            Assert.Equal(new[] { "scope1" }, svc1.Scopes);
            Assert.Equal(new[] { "scope2" }, svc2.Scopes);
        }

        [Fact]
        public void CreateWithUser()
        {
            var serviceAccountCred = new ServiceAccountCredential(new ServiceAccountCredential.Initializer("MyId", "MyTokenServerUrl")
            {
                Clock = new MockClock(DateTime.UtcNow),
                AccessMethod = new DummyAccessMethod(),
                HttpClientFactory = new DummyHttpClientFactory(),
                DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.Exception, // This is not the default
                ProjectId = "a_project_id",
                User = "user1",
                Scopes = new[] { "scope1" },
            }.FromPrivateKey(PrivateKey));
            var cred1 = GoogleCredential.FromServiceAccountCredential(serviceAccountCred);
            var cred2 = cred1.CreateWithUser("user2");

            var svc1 = (ServiceAccountCredential)cred1.UnderlyingCredential;
            var svc2 = (ServiceAccountCredential)cred2.UnderlyingCredential;

            Assert.Same(serviceAccountCred, svc1);
            Assert.NotSame(serviceAccountCred, svc2);

            Assert.Same(svc1.Id, svc2.Id);
            Assert.Same(svc1.TokenServerUrl, svc2.TokenServerUrl);
            Assert.Same(svc1.Clock, svc2.Clock);
            Assert.Same(svc1.AccessMethod, svc2.AccessMethod);
            Assert.Same(svc1.HttpClientFactory, svc2.HttpClientFactory);
            Assert.Equal(svc1.DefaultExponentialBackOffPolicy, svc2.DefaultExponentialBackOffPolicy);
            Assert.Same(svc1.ProjectId, svc2.ProjectId);
            Assert.NotSame(svc1.User, svc2.User);
            Assert.Collection(svc1.Scopes, scope => Assert.Equal("scope1", scope));
            Assert.Collection(svc2.Scopes, scope => Assert.Equal("scope1", scope));
            Assert.Equal("user1", svc1.User);
            Assert.Equal("user2", svc2.User);
        }

        [Fact]
        public void WithHttpClientFactory()
        {
            var credential = new ServiceAccountCredential(new ServiceAccountCredential.Initializer("MyId").FromPrivateKey(PrivateKey));
            var factory = new HttpClientFactory();
            var credentialWithFactory = Assert.IsType<ServiceAccountCredential>(((IGoogleCredential)credential).WithHttpClientFactory(factory));

            Assert.NotSame(credential, credentialWithFactory);
            Assert.NotSame(credential.HttpClient, credentialWithFactory.HttpClient);
            Assert.NotSame(credential.HttpClientFactory, credentialWithFactory.HttpClientFactory);
            Assert.Same(factory, credentialWithFactory.HttpClientFactory);
        }

        [Fact]
        public void WithUseJwtAccessWithScopes()
        {
            var credential = new ServiceAccountCredential(new ServiceAccountCredential.Initializer("MyId").FromPrivateKey(PrivateKey));
            Assert.False(credential.UseJwtAccessWithScopes);

            var credentialWithJwtFlag = credential.WithUseJwtAccessWithScopes(true);

            Assert.NotSame(credential, credentialWithJwtFlag);
            Assert.Same(credential.Id, credentialWithJwtFlag.Id);
            Assert.True(credentialWithJwtFlag.UseJwtAccessWithScopes);
        }

        [Fact]
        public async Task FetchesOidcToken()
        {
            // A little bit after the tokens returned from OidcTokenFakes were issued.
            var clock = new MockClock(new DateTime(2020, 5, 13, 15, 0, 0, 0, DateTimeKind.Utc));
            var messageHandler = new OidcTokenResponseSuccessMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer("MyId", "http://will.be.ignored")
            {
                Clock = clock,
                ProjectId = "a_project_id",
                HttpClientFactory = new MockHttpClientFactory(messageHandler)
            };
            var credential = new ServiceAccountCredential(initializer.FromPrivateKey(PrivateKey));

            // The fake Oidc server returns valid tokens (expired in the real world for safety)
            // but with a set audience that lets us know if the token was refreshed or not.
            var oidcToken = await credential.GetOidcTokenAsync(OidcTokenOptions.FromTargetAudience("will.be.ignored"));

            var signedToken = SignedToken<Header, Payload>.FromSignedToken(await oidcToken.GetAccessTokenAsync());
            Assert.Equal("https://first_call.test", signedToken.Payload.Audience);
            // Move the clock some but not enough that the token expires.
            clock.UtcNow = clock.UtcNow.AddMinutes(20);
            signedToken = SignedToken<Header, Payload>.FromSignedToken(await oidcToken.GetAccessTokenAsync());
            Assert.Equal("https://first_call.test", signedToken.Payload.Audience);
            // Only the first call should have resulted in a request. The second time the token hadn't expired.
            Assert.Equal(1, messageHandler.Calls);
        }

        [Fact]
        public async Task RefreshesOidcToken()
        {
            // A little bit after the tokens returned from OidcTokenFakes were issued.
            var clock = new MockClock(new DateTime(2020, 5, 13, 15, 0, 0, 0, DateTimeKind.Utc));
            var messageHandler = new OidcTokenResponseSuccessMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer("MyId", "http://will.be.ignored")
            {
                Clock = clock,
                ProjectId = "a_project_id",
                HttpClientFactory = new MockHttpClientFactory(messageHandler)
            };
            var credential = new ServiceAccountCredential(initializer.FromPrivateKey(PrivateKey));

            var oidcToken = await credential.GetOidcTokenAsync(OidcTokenOptions.FromTargetAudience("audience"));

            var signedToken = SignedToken<Header, Payload>.FromSignedToken(await oidcToken.GetAccessTokenAsync());
            Assert.Equal("https://first_call.test", signedToken.Payload.Audience);
            // Move the clock so that the token expires.
            clock.UtcNow = clock.UtcNow.AddHours(2);
            signedToken = SignedToken<Header, Payload>.FromSignedToken(await oidcToken.GetAccessTokenAsync());
            Assert.Equal("https://subsequent_calls.test", signedToken.Payload.Audience);
            // Two calls, because the second time we tried to get the token, the first one had expired.
            Assert.Equal(2, messageHandler.Calls);
        }

        [Fact]
        public async Task FetchesOidcToken_CorrectPayloadSent()
        {
            // A little bit after the tokens returned from OidcTokenFakes were issued.
            var clock = new MockClock(new DateTime(2020, 5, 13, 15, 0, 0, 0, DateTimeKind.Utc));
            var messageHandler = new OidcTokenResponseSuccessMessageHandler();
            var initializer = new ServiceAccountCredential.Initializer("MyId", "http://will.be.ignored")
            {
                Clock = clock,
                ProjectId = "a_project_id",
                HttpClientFactory = new MockHttpClientFactory(messageHandler)
            };
            var credential = new ServiceAccountCredential(initializer.FromPrivateKey(PrivateKey));

            var expectedPayload = new JsonWebSignature.Payload
            {
                Issuer = "MyId",
                Subject = "MyId",
                Audience = GoogleAuthConsts.OidcTokenUrl,
                IssuedAtTimeSeconds = (long)(clock.UtcNow - UnixEpoch).TotalSeconds,
                ExpirationTimeSeconds = (long)(clock.UtcNow.Add(JwtLifetime) - UnixEpoch).TotalSeconds,
                TargetAudience = "any_audience"
            };
            var serializedExpectedPayload = NewtonsoftJsonSerializer.Instance.Serialize(expectedPayload);
            var urlSafeEncodedExpectedPayload = TokenEncodingHelpers.UrlSafeBase64Encode(serializedExpectedPayload);

            var oidcToken = await credential.GetOidcTokenAsync(OidcTokenOptions.FromTargetAudience("any_audience"));
            await oidcToken.GetAccessTokenAsync();

            var requestFormDict = messageHandler.LatestRequestContent.Split('&')
                .ToDictionary(entry => entry.Split('=')[0], entry => entry.Split('=')[1]);
            var assertion = requestFormDict["assertion"];
            // Assertion format shoould be headers.payload.signature
            var encodedPayload = assertion.Split('.')[1];

            Assert.Contains(urlSafeEncodedExpectedPayload, encodedPayload);
        }

        private static string GetContents(string fileName)
        {
            var resourceName = $"Google.Apis.Auth.Tests.OAuth2.DummyCredentialFiles.{fileName}";

            using var stream = CurrentAssembly.GetManifestResourceStream(resourceName) ?? throw new FileNotFoundException();
            using var streamReader = new StreamReader(stream);

            return streamReader.ReadToEnd();
        }
    }
}
