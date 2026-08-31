using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using Readarr.Http;

namespace Readarr.Api.V1.Author
{
    [V1ApiController("moveauthor")]
    public class MoveAuthorController : Controller
    {
        private readonly IAuthorMovePreviewService _movePreviewService;

        public MoveAuthorController(IAuthorMovePreviewService movePreviewService)
        {
            _movePreviewService = movePreviewService;
        }

        // GET /api/v1/moveauthor?authorId=1                    - one author
        // GET /api/v1/moveauthor?authorIds=1&authorIds=2       - a selection
        //
        // There is deliberately no "preview everything" mode. Building a preview costs one
        // file query per author, so an implicit whole-library call would be tens of
        // thousands of queries from a single click. Callers name the authors they mean.
        [HttpGet]
        public List<MoveAuthorResource> GetMovePreviews(int? authorId, [FromQuery] List<int> authorIds)
        {
            var ids = new List<int>();

            if (authorId.HasValue)
            {
                ids.Add(authorId.Value);
            }

            if (authorIds != null)
            {
                ids.AddRange(authorIds);
            }

            if (!ids.Any())
            {
                return new List<MoveAuthorResource>();
            }

            return _movePreviewService.GetMovePreviews(ids.Distinct().ToList()).ToResource();
        }
    }
}
