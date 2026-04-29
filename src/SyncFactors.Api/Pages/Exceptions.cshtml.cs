using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SyncFactors.Api.Pages;

public sealed class ExceptionsModel(ExceptionQueueQueryService queryService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? QueueType { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public ExceptionQueueResult Queue { get; private set; } = new(
        QueueType: null,
        Search: null,
        Items: [],
        Summary: new Dictionary<string, int>(),
        Total: 0,
        Page: 1,
        PageSize: ExceptionQueueQueryService.DefaultPageSize,
        TotalPages: 1);

    public IReadOnlyList<ExceptionQueueType> QueueTypes => ExceptionQueueQueryService.QueueTypes;

    public bool HasPreviousPage => Queue.Page > 1;

    public bool HasNextPage => Queue.Page < Queue.TotalPages;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Queue = await queryService.LoadAsync(QueueType, Search, PageNumber, ExceptionQueueQueryService.DefaultPageSize, cancellationToken);
        QueueType = Queue.QueueType;
        PageNumber = Queue.Page;
    }

    public int GetSummaryCount(string key) =>
        Queue.Summary.TryGetValue(key, out var count) ? count : 0;
}
