using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Serialization;

namespace GapFinder.Arr
{
    /// <summary>Error calling Sonarr/Radarr, with a message that is readable on the page.</summary>
    public class ArrException : Exception
    {
        public ArrException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Minimal client for the Sonarr/Radarr API v3 (header X-Api-Key). Runs on the Emby server - the API key
    /// never leaves the server towards the browser.
    /// </summary>
    public class ArrClient
    {
        // One HttpClient for the whole plugin (recommended use, avoids socket exhaustion).
        // No automatic redirects: .NET only strips "Authorization" on redirects, not X-Api-Key, so the key would be
        // sent to the host of the Location header. The timeout is applied per call (RequestTimeout) so it also
        // covers reading the response.
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Largest accepted response. Episode lists of very long series are a few MB.</summary>
        internal const int MaxResponseBytes = 32 * 1024 * 1024;

        private readonly string _service;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly IJsonSerializer _json;

        public ArrClient(string service, string baseUrl, string apiKey, IJsonSerializer json)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArrException(service + ": no address configured.");
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArrException(service + ": no API key configured.");
            }
            _service = service;
            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;
            _json = json;
        }

        public Task<T> GetAsync<T>(string path, CancellationToken token) where T : class =>
            SendAsync<T>(HttpMethod.Get, path, null, token);

        public Task<T> PostAsync<T>(string path, object body, CancellationToken token) where T : class =>
            SendAsync<T>(HttpMethod.Post, path, body, token);

        public Task<T> PutAsync<T>(string path, object body, CancellationToken token) where T : class =>
            SendAsync<T>(HttpMethod.Put, path, body, token);

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken token) where T : class
        {
            using (var request = new HttpRequestMessage(method, _baseUrl + "/api/v3/" + path.TrimStart('/')))
            {
                request.Headers.Add("X-Api-Key", _apiKey);
                request.Headers.Accept.ParseAdd("application/json");
                if (body != null)
                {
                    request.Content = new StringContent(_json.SerializeToString(body), Encoding.UTF8, "application/json");
                }

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(RequestTimeout);
                    try
                    {
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        {
                            var text = await ReadLimitedAsync(response, timeout.Token).ConfigureAwait(false);
                            return Parse<T>(response, text);
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new ArrException(_service + " did not answer within 30 seconds (" + _baseUrl + ").");
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new ArrException(_service + " is not reachable at " + _baseUrl + ": " + (ex.InnerException?.Message ?? ex.Message));
                    }
                }
            }
        }

        /// <summary>Reads the response up to MaxResponseBytes - a malicious or wrong endpoint must not fill Emby with huge answers.</summary>
        private async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (response.Content == null)
            {
                return string.Empty;
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw TooLarge();
            }
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new System.IO.MemoryStream())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxResponseBytes)
                    {
                        throw TooLarge();
                    }
                    buffer.Write(chunk, 0, read);
                }
                var charset = response.Content.Headers.ContentType?.CharSet;
                Encoding encoding;
                try
                {
                    encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
                }
                catch (ArgumentException)
                {
                    encoding = Encoding.UTF8;
                }
                return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }

        private ArrException TooLarge() =>
            new ArrException(_service + " answer is too large (over " + MaxResponseBytes / (1024 * 1024) + " MB) - is " + _baseUrl + " really " + _service + "?");

        private T Parse<T>(HttpResponseMessage response, string text) where T : class
        {
            var status = (int)response.StatusCode;
            if (status >= 300 && status < 400)
            {
                // Do not follow redirects (see Http): name the target without credentials/query so the admin can enter the right address.
                var location = response.Headers.Location;
                var target = location == null
                    ? "an unknown address"
                    : Shorten((location.IsAbsoluteUri ? location : new Uri(new Uri(_baseUrl + "/"), location)).GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));
                throw new ArrException(_service + " answered with a redirect (HTTP " + status + ") to " + target +
                    ". Please enter the final address of " + _service + " directly - the API key is not sent to redirected addresses.");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ArrException(_service + " rejected the API key (HTTP 401).");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ArrException(_service + " answered HTTP " + status + ": " + Shorten(text));
            }
            if (typeof(T) == typeof(string))
            {
                return text as T;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return _json.DeserializeFromString<T>(text);
            }
            catch (Exception)
            {
                // Typical case: the address points to another site (HTML instead of JSON).
                throw new ArrException(_service + " returned an unexpected answer - is " + _baseUrl + " really " + _service + "?");
            }
        }

        private static string Shorten(string text)
        {
            text = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 200 ? text.Substring(0, 200) + "..." : text;
        }
    }
}
