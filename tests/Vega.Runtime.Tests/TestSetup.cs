using NUnit.Framework;
using Terraria;
using Terraria.Initializers;
using Terraria.Localization;

namespace Vega.Runtime.Tests;

[SetUpFixture]
public class TestSetup
{
	/// <summary>
	/// Initializes the Terraria localization state used by integration-boundary tests.
	/// </summary>
	[OneTimeSetUp]
	public static void SetupTerraria()
	{
		ChatInitializer.Load();

		Program.SavePath = "";
		LanguageManager.Instance.SetLanguage(GameCulture.DefaultCulture);
		Lang.InitializeLegacyLocalization();
	}
}
