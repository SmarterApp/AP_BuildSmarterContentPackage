using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

// From GitLab API Documentation Here: https://docs.gitlab.com/ce/api/

namespace BuildSmarterContentPackage
{
    /// <summary>
    /// C# Connection to the GitLab API
    /// </summary>
    class GitLab
    {
        const string c_GitLabApiPath = "/api/v4/";

        // GitLab API max items per page is 100.
        const int c_filesPerPage = 100;

        Uri m_baseAddress;
        string m_accessToken;

        public GitLab(string serverUrl, string accessToken)
        {
            m_baseAddress = new Uri(serverUrl);
            m_accessToken = accessToken;
        }

        /// <summary>
        /// Get the ID that corresponds to a project (repository) name. 
        /// </summary>
        /// <param name="ns">The namespace (username or group name) that owns the project.</param>
        /// <param name="name">The name of the project.</param>
        /// <returns>The project ID.</returns>
        public string ProjectIdFromName(string ns, string name)
        {
            UriBuilder uri = new UriBuilder(m_baseAddress);
            uri.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(string.Concat(ns, "/", name)));
            var doc = HttpReceiveJson(uri.Uri);
            return doc.Element("id").Value;
        }

        /// <summary>
        /// List the blobs (files) that belong to a project (repository).
        /// </summary>
        /// <param name="projectId">The project ID for which to list files.</param>
        /// <returns>A list of key value pairs. The keys are the names of the blobs (files). The values are the IDs.</returns>
        public IReadOnlyList<KeyValuePair<string, string>> ListRepositoryTree(string projectId)
        {

            var result = new List<KeyValuePair<string, string>>();

            UriBuilder ub = new UriBuilder(m_baseAddress);
            ub.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(projectId), "/repository/tree");
            ub.Query = $"recursive=true&per_page={c_filesPerPage}";

            // This API is paginated, it may require multiple requests to retrieve all items.
            Uri uri = ub.Uri;
            int totalExpectedFiles = 0;
            int page = 1;   // GitLab numbers pages starting with 1
            while (uri != null)
            {
                System.Diagnostics.Debug.WriteLine(uri.ToString());

                var request = HttpPrepareRequest(uri);
                using (var response = HttpGetResponseHandleErrors(request))
                {
                    // Get the total expected files
                    int.TryParse(response.GetResponseHeader("X-Total"), out totalExpectedFiles);

                    // Get the returned page number and check to make sure it was the right one.
                    int pageReturned = 0;
                    int.TryParse(response.GetResponseHeader("X-Page"), out pageReturned);
                    if (pageReturned != page)
                    {
                        throw new ApplicationException($"GitLab returned page {pageReturned} expected {page}");
                    }
                    ++page;

                    // Get the next page URI (returns null if no more pages)
                    uri = HttpNextPageUri(response);

                    // Retrieve the files
                    var doc = HttpReceiveJson(response);
                    foreach (var el in doc.Elements("item"))
                    {
                        result.Add(new KeyValuePair<string, string>(el.Element("path").Value, el.Element("id").Value));
                    }
                }
            }

            if (result.Count != totalExpectedFiles)
            {
                throw new ApplicationException($"Expected {totalExpectedFiles} files in item but received {result.Count}");
            }

            return result;
        }

        /// <summary>
        /// Reads a blob (file) from a project on GitLab.
        /// </summary>
        /// <param name="projectId">The id of the project containing the blob.</param>
        /// <param name="blobId">The id of the blob (not the name).</param>
        /// <returns>A read-forward stream with the contents of the blob.</returns>
        /// <remarks>
        /// Be sure to dispose the stream when reading is complete.
        /// </remarks>
        public Stream ReadBlob(string projectId, string blobId)
        {
            UriBuilder uri = new UriBuilder(m_baseAddress);
            uri.Path = string.Concat(c_GitLabApiPath, "projects/", Uri.EscapeDataString(projectId), "/repository/blobs/", blobId, "/raw");

            try
            {
                HttpWebRequest request = HttpPrepareRequest(uri.Uri);
                WebResponse response = HttpGetResponseHandleErrors(request);
                return response.GetResponseStream(); // Closing the stream also closes the response so we don't need to dispose response
            }
            catch (WebException ex)
            {
                throw ConvertWebException(ex);
            }
        }

        private HttpWebRequest HttpPrepareRequest(Uri uri)
        {
            HttpWebRequest request = WebRequest.CreateHttp(uri);
            request.Headers["PRIVATE-TOKEN"] = m_accessToken;
            return request;
        }

        private static HttpWebResponse HttpGetResponseHandleErrors(HttpWebRequest request)
        {
            try
            {
                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                throw ConvertWebException(ex);
            }
        }

        private static XElement HttpReceiveJson(HttpWebResponse response)
        {
            XElement doc = null;
            using (var stream = response.GetResponseStream())
            {
                using (var jsonReader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader(stream, new System.Xml.XmlDictionaryReaderQuotas()))
                {
                    doc = XElement.Load(jsonReader);
                }
            }
            return doc;
        }

        private XElement HttpReceiveJson(Uri uri)
        {
            HttpWebRequest request = HttpPrepareRequest(uri);
            using (var response = HttpGetResponseHandleErrors(request))
            {
                return HttpReceiveJson(response);
            }
        }

        private Uri HttpNextPageUri(HttpWebResponse response)
        {
            var parser = new HttpLinkHeaderParser(response);
            while (parser.MoveNext())
            {
                if ((parser.CurrentParameters["rel"] ?? string.Empty).Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri(parser.CurrentUrl);
                }
            }
            return null;
        }

        private static Exception ConvertWebException(WebException ex)
        {
            HttpWebResponse response = null;
            try
            {
                response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    string detail;
                    using (var reader = new System.IO.StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        detail = reader.ReadToEnd();
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new HttpNotFoundException(string.Concat("HTTP Resource Not Found: ", response.ResponseUri, "\r\n", detail));
                    }
                    else
                    {
                        return new ApplicationException(string.Concat("HTTP ERROR\r\n", detail));
                    }
                }
                return new ApplicationException("HTTP ERROR", ex);
            }
            finally
            {
                if (response != null)
                {
                    response.Dispose();
                    response = null;
                }
            }
        }

        static void DumpXml(XElement xml)
        {
            var settings = new System.Xml.XmlWriterSettings();
            settings.Indent = true;
            settings.OmitXmlDeclaration = true;
            using (var writer = System.Xml.XmlWriter.Create(Console.Error, settings))
            {
                xml.WriteTo(writer);
            }
            Console.Error.WriteLine();
        }
    }

    class HttpNotFoundException : Exception
    {
        public HttpNotFoundException(string message)
            : base(message)
        {
        }

        public HttpNotFoundException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Parse a link header value according to http://www.rfc-editor.org/rfc/rfc5988.txt
    /// </summary>
    class HttpLinkHeaderParser
    {
        string m_header;
        int m_cursor;

        string m_currentUrl;
        Dictionary<string, string> m_currentParameters = new Dictionary<string, string>();

        public HttpLinkHeaderParser(string linkHeaderValue)
        {
            m_header = linkHeaderValue;
            m_cursor = 0;
        }

        public HttpLinkHeaderParser(HttpWebResponse response)
        {
            m_header = response.GetResponseHeader("Link");
            m_cursor = 0;
        }

        public string CurrentUrl
        {
            get { return m_currentUrl; }
        }

        public IReadOnlyDictionary<string, string> CurrentParameters
        {
            get { return m_currentParameters; }
        }

        public bool MoveNext()
        {
            m_currentParameters.Clear();
            int end = m_header.Length;
            SkipWhiteSpace();

            // URL should be next (enclosed in angle brackets)
            if (m_cursor >= end || m_header[m_cursor] != '<')
            {
                m_cursor = end;
                return false;
            }
            ++m_cursor;

            // Parse the URL
            m_currentUrl = ParseUntil('>');
            if (m_cursor < end) ++m_cursor;

            SkipWhiteSpace();

            // Parse parameters
            while (m_cursor < end && m_header[m_cursor] == ';')
            {
                ++m_cursor;
                SkipWhiteSpace();

                // Parse parameter name
                string parmName = ParseUntil(s_spaceOrEq);
                SkipWhiteSpace();

                // Parse parameter value
                string parmValue = string.Empty;
                if (m_cursor < end && m_header[m_cursor] == '=')
                {
                    ++m_cursor;
                    SkipWhiteSpace();

                    // Quoted string value
                    if (m_cursor < end && m_header[m_cursor] == '"')
                    {
                        parmValue = ParseQuotedString();
                    }
                    else
                    {
                        parmValue = ParseUntil(s_spaceCommaOrSemi);
                    }
                }
                m_currentParameters.Add(parmName.ToLower(), parmValue);

                SkipWhiteSpace();
            }

            // Move to next input (if any)
            if (m_cursor < end && m_header[m_cursor] == ',')
            {
                ++m_cursor;
            }
            else
            {
                m_cursor = end;
            }

            return true;
        }

        private void SkipWhiteSpace()
        {
            while (m_cursor < m_header.Length && char.IsWhiteSpace(m_header[m_cursor])) ++m_cursor;
        }

        private string ParseUntil(char c)
        {
            int i = m_header.IndexOf(c, m_cursor);
            if (i < 0) i = m_header.Length;
            string value = m_header.Substring(m_cursor, i - m_cursor);
            m_cursor = i;
            return value;
        }

        private string ParseUntil(params char[] anyOf)
        {
            int i = m_header.IndexOfAny(anyOf, m_cursor);
            if (i < 0) i = m_header.Length;
            string value = m_header.Substring(m_cursor, i - m_cursor);
            m_cursor = i;
            return value;
        }

        static readonly char[] s_spaceOrGt = new char[] { '>', ' ', '\r', '\n', '\t' };
        static readonly char[] s_spaceOrEq = new char[] { '=', ' ', '\r', '\n', '\t' };
        static readonly char[] s_spaceCommaOrSemi = new char[] { ',', ';', ' ', '\r', '\n', '\t' };

        private string ParseQuotedString()
        {
            if (m_header[m_cursor] != '"') return string.Empty;

            var sb = new StringBuilder();
            int end = m_header.Length;
            int i = m_cursor + 1;
            while (i < end && m_header[i] != '"')
            {
                // Handle backslash escaping
                if (m_header[i] == '\\') ++i;
                sb.Append(m_header[i]);
                ++i;
            }
            if (i < end) ++i; // Skip closing quote
            m_cursor = i;
            return sb.ToString();
        }
        
    }
}
