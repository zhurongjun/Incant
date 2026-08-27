using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SB.Core
{
    public interface IArtifact
    {
        public bool IsRestored { get; }
    }

    public class PlainArtifact : IArtifact
    {
        public bool IsRestored { get; init; }
    }
}
