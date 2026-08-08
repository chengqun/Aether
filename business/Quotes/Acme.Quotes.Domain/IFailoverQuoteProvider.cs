using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Quotes.Domain;

public interface IFailoverQuoteProvider
{
    Task<List<Quote>> GetKlineAsync(string code, string freq, string type, string exchange, CancellationToken ct = default);
}
