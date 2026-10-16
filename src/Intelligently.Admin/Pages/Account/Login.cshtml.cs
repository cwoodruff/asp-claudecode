using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Intelligently.Admin.Pages.Account;

/// <summary>Cookie sign-in with an Identity user name and password. Accounts are provisioned out of band.</summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly SignInManager<IdentityUser> _signIn;

    public LoginModel(SignInManager<IdentityUser> signIn)
    {
        _signIn = signIn;
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public string? ReturnUrl { get; private set; }
    public string? Error { get; private set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        if (!ModelState.IsValid) return Page();

        var result = await _signIn.PasswordSignInAsync(Input.UserName, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");

        Error = result.IsLockedOut ? "This account is locked out. Try again later." : "Invalid user name or password.";
        return Page();
    }

    public sealed class InputModel
    {
        [Required] public string UserName { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        public bool RememberMe { get; set; }
    }
}
