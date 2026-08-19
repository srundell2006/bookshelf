using System;
using System.Collections.Generic;
using System.Net;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Http;

namespace NzbDrone.Core.MetadataSource.Goodreads
{
    public interface IGoodreadsSearchProxy
    {
        public List<SearchJsonResource> Search(string query);
    }

    public class GoodreadsSearchProxy : IGoodreadsSearchProxy
    {
        private readonly ICachedHttpResponseService _cachedHttpClient;
        private readonly IMetadataRequestBuilder _metadataRequestBuilder;
        private readonly Logger _logger;

        public GoodreadsSearchProxy(ICachedHttpResponseService cachedHttpClient,
            IMetadataRequestBuilder metadataRequestBuilder,
            Logger logger)
        {
            _cachedHttpClient = cachedHttpClient;
            _metadataRequestBuilder = metadataRequestBuilder;
            _logger = logger;
        }

        public List<SearchJsonResource> Search(string query)
        {
            try
            {
                var httpRequest = _metadataRequestBuilder.GetRequestBuilder().Create()
                    .SetSegment("route", "search")
                    .AddQueryParam("q", query)
                    .Build();

                // Read the cache, not just write it.  Search is by far the slowest call to the
                // metadata server (hundreds of ms against ~15ms for a lookup by id) and during a
                // library scan the same queries recur constantly - most of them returning nothing,
                // because files whose names do not parse cleanly all ask again and again.  The
                // five day entry was already being written on every response; passing false here
                // meant it was never read, so every repeat paid full price.
                var response = _cachedHttpClient.Get<List<SearchJsonResource>>(httpRequest, true, TimeSpan.FromDays(5));

                return response.Resource;
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Unable to communicate with metadata source.", ex, query);
            }
            catch (WebException ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Unable to communicate with metadata source.", ex, query, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex);
                throw new GoodreadsException("Search for '{0}' failed. Invalid response received from metadata source.", ex, query);
            }
        }
    }
}
