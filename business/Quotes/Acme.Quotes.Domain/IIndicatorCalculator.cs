using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.Quotes.Domain;

public interface IIndicatorCalculator
{
    Task<string> CalculateAsCsvAsync(List<Quote> quotes, string indicators, int rowCount = 60);
}
