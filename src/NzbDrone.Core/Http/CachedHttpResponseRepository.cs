using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Http
{
    public interface ICachedHttpResponseRepository : IBasicRepository<CachedHttpResponse>
    {
        CachedHttpResponse FindByUrl(string url);
    }

    public class CachedHttpResponseRepository : BasicRepository<CachedHttpResponse>, ICachedHttpResponseRepository
    {
        public CachedHttpResponseRepository(ICacheDatabase database,
                                            IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public CachedHttpResponse FindByUrl(string url)
        {
            var matches = Query(x => x.Url == url)
                .OrderByDescending(x => x.LastRefresh)
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            // The cache is populated by a read-then-insert in CachedHttpResponseService, so
            // concurrent callers can each miss and insert their own row for the same URL.
            // This used to be SingleOrDefault, which meant one duplicate permanently broke
            // every lookup for that URL with "Sequence contains more than one element" --
            // surfacing as a 500 on anything that fetched metadata. Keep the freshest row
            // and drop the strays so the table heals itself instead.
            if (matches.Count > 1)
            {
                DeleteMany(matches.Skip(1).Select(x => x.Id).ToList());
            }

            return matches[0];
        }
    }
}
