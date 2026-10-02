using System.Threading.Tasks;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// Plugin API kept for compatibility: community plugins (PaperMC, ForgeMC, …) call
    /// <c>UI.CreateYesNoPromptV1</c> to ask for EULA agreement during install. The legacy version showed a
    /// dialog on the WPF window; the engine routes the question through <see cref="UserPrompt"/> instead —
    /// answered by the consents given when the install was started, or live by a user, otherwise "no".
    /// </summary>
    public static class UI
    {
        public static Task<bool> CreateYesNoPromptV1(string title, string message, string affirmativeButtonText, string negativeButtonText)
        {
            return UserPrompt.ConfirmAsync(UserPrompt.KeyForTitle(title), title, message);
        }
    }
}
