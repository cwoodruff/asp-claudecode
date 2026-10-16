using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelligently.Admin.Pages.Account;

/// <summary>POST-only sign-out (the layout's form carries the antiforgery token); GET just goes home.</summary>
public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<IdentityUser> _signIn;

    public LogoutModel(SignInManager<IdentityUser> signIn)
    {
        _signIn = signIn;
    }

    public IActionResult OnGet() => LocalRedirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await _signIn.SignOutAsync();
        return LocalRedirect("/");
    }
}
