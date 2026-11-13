using EliteSIP.App.Resources;

namespace EliteSIP.App.FirstRun;

public sealed partial class FirstRunViewModel
{
    public bool ShowsConnection => Step is FirstRunStep.Welcome or FirstRunStep.Pair or FirstRunStep.User;
    public bool ShowsPairing => ShowsConnection && !ShowsUser;
    public bool CanReturn => Step is FirstRunStep.Appearance or FirstRunStep.Finale;
    public string PageTitle => Strings.Get(ShowsConnection ? "AdminConnection" : ShowsAppearance ? "SectionAppearance" : "WizardReady");
    public string ProfileNumber => IsPaired ? PairedNumber : Username;
    public string ProfileLabel => IsPaired ? PairedEmployee : DisplayName;
    public string ProfileSource => Strings.Get(IsPaired || ShowsPairing ? "WizardFromSpark" : "WizardLocal");
    public string FooterNote => LanguageChanges ? Strings.Get("SettingsRestartRequired") : ProfileSource;

    public void Begin() { if (Step is FirstRunStep.Welcome) { Step = FirstRunStep.Pair; } }
    public void UseSparkSetup() => Step = FirstRunStep.Pair;

    private void NotifyLayout()
    {
        foreach (var name in new[] { nameof(ShowsConnection), nameof(ShowsPairing), nameof(CanReturn), nameof(PageTitle),
            nameof(ProfileNumber), nameof(ProfileLabel), nameof(ProfileSource), nameof(FooterNote) })
        {
            NotifyChanged(name);
        }
    }
}
