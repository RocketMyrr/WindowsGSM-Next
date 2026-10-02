using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Plugin questions (EULA etc.) are answered by consents granted when the operation started — never by
/// default, and never leaking into other concurrent operations.
/// </summary>
[Collection("UserPrompt")] // AsyncHandler is process-wide; don't run these in parallel with each other
public class UserPromptTests
{
    [Fact]
    public async Task Ungranted_question_defaults_to_no()
    {
        UserPrompt.AsyncHandler = null;
        Assert.False(await UserPrompt.ConfirmAsync(UserPrompt.Keys.Eula, "EULA", "Agree?"));
    }

    [Fact]
    public async Task Granted_consent_answers_yes_inside_the_scope_only()
    {
        UserPrompt.AsyncHandler = null;
        using (UserPrompt.WithConsents(new[] { UserPrompt.Keys.Eula }))
        {
            Assert.True(await UserPrompt.ConfirmAsync(UserPrompt.Keys.Eula, "EULA", "Agree?"));
            Assert.False(await UserPrompt.ConfirmAsync(UserPrompt.Keys.InstallJava, "Java", "Install?"));
        }
        Assert.False(await UserPrompt.ConfirmAsync(UserPrompt.Keys.Eula, "EULA", "Agree?"));
    }

    [Fact]
    public async Task Consent_does_not_leak_to_a_concurrent_operation()
    {
        UserPrompt.AsyncHandler = null;
        var gate = new TaskCompletionSource();
        bool otherAnswer = true;

        Task other = Task.Run(async () =>
        {
            await gate.Task;
            otherAnswer = await UserPrompt.ConfirmAsync(UserPrompt.Keys.Eula, "EULA", "Agree?");
        });

        using (UserPrompt.WithConsents(new[] { UserPrompt.Keys.Eula }))
        {
            gate.SetResult();
            await other;
        }
        Assert.False(otherAnswer);
    }

    [Fact]
    public async Task Legacy_plugin_prompt_API_uses_the_EULA_consent()
    {
        UserPrompt.AsyncHandler = null;
        using (UserPrompt.WithConsents(new[] { UserPrompt.Keys.Eula }))
        {
            Assert.True(await UI.CreateYesNoPromptV1("Agreement to the EULA", "By continuing…", "Agree", "Decline"));
        }
        Assert.False(await UI.CreateYesNoPromptV1("Agreement to the EULA", "By continuing…", "Agree", "Decline"));
    }

    [Fact]
    public async Task Unexpected_question_goes_to_the_live_handler()
    {
        string? askedKey = null;
        UserPrompt.AsyncHandler = (key, title, message) => { askedKey = key; return Task.FromResult(true); };
        try
        {
            Assert.True(await UI.CreateYesNoPromptV1("Choose world size", "Large?", "Yes", "No"));
            Assert.Equal("prompt:choose world size", askedKey);
        }
        finally { UserPrompt.AsyncHandler = null; }
    }
}
