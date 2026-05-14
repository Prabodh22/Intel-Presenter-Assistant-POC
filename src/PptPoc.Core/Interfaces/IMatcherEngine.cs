using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface IMatcherEngine
{
    List<MatchResult> Match(string transcriptText, SlideSnapshot snapshot);
}
