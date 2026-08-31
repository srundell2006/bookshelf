using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Books.Commands
{
    public class MoveAuthorToLetterCommand : Command
    {
        public MoveAuthorToLetterCommand()
        {
            AuthorIds = new List<int>();
        }

        public List<int> AuthorIds { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool RequiresDiskAccess => true;
    }
}
