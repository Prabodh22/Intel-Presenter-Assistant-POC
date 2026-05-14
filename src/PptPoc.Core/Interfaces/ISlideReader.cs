using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface ISlideReader
{
    SlideSnapshot ReadSlide(object slideComObject);
}
