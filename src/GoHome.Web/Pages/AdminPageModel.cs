using GoHome.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GoHome.Web.Pages;

public abstract class AdminPageModel : PageModel
{
    protected const string Actor = "admin";

    protected IActionResult Done(OpResult r, object? routeValues = null)
    {
        TempData["Message"] = r.Message;
        TempData["Ok"] = r.Ok;
        return RedirectToPage(routeValues);
    }

    protected IActionResult Done(string message, object? routeValues = null) => Done(OpResult.Success(message), routeValues);
}
