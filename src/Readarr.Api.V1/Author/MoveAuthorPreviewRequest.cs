using System.Collections.Generic;

namespace Readarr.Api.V1.Author
{
    // Body for POST /api/v1/moveauthor. The ids travel in a body rather than the query
    // string because a library-sized selection exceeds the server's request-line limit.
    public class MoveAuthorPreviewRequest
    {
        public List<int> AuthorIds { get; set; }
    }
}
