using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

public sealed class LookupModel(SuccessFactorsUserLookupService lookupService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? LookupValue { get; set; }

    public SuccessFactorsUserLookupResult? Result { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(LookupValue))
        {
            return;
        }

        try
        {
            Result = await lookupService.LookupAsync(LookupValue, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            ErrorMessage = ex.Message;
        }
    }
}
