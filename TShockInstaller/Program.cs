using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine($"TShock Installer {typeof(Program).Assembly.GetName().Version}.");

Console.WriteLine("Determining dotnet runtime url...");

var arch = RuntimeInformation.ProcessArchitecture switch
{
	Architecture.X64 => "x64",
	Architecture.Arm64 => "arm64",
	_ => null
};

if (arch is null)
{
	Console.WriteLine($"{RuntimeInformation.ProcessArchitecture} is not yet supported via this installer.");
	return;
}

const string runtimeVersion = "10.0.11";
string? url = null;
if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
	url = $"https://dotnetcli.azureedge.net/dotnet/Runtime/{runtimeVersion}/dotnet-runtime-{runtimeVersion}-osx-{arch}.tar.gz";
else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
	url = $"https://dotnetcli.azureedge.net/dotnet/Runtime/{runtimeVersion}/dotnet-runtime-{runtimeVersion}-win-{arch}.zip";
else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
	url = $"https://dotnetcli.azureedge.net/dotnet/Runtime/{runtimeVersion}/dotnet-runtime-{runtimeVersion}-linux-{arch}.tar.gz";

if (url is null)
{
	Console.WriteLine("Unable to determine .NET runtime to install. " +
		"Refer to https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script " +
		"and install using --install-dir dotnet, so that the dotnet folder is beside TShock.Server[.exe]");
	return;
}

Console.WriteLine("Using url: " + url);

var filename = url.Split('/').Last();
var isTarGz = filename.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

var downloadInfo = new FileInfo(filename);
if (!downloadInfo.Exists)
{
	Console.WriteLine($"Downloading: {filename}...");

	using var client = new HttpClient();
	await using var resp = await client.GetStreamAsync(url);
	await using var fs = new FileStream(filename, FileMode.Create);
	await resp.CopyToAsync(fs);
}
else
{
	Console.WriteLine("Using existing download on disk: " + filename);
}

var dotnetPath = Path.Combine("dotnet", "dotnet" + (isTarGz ? "" : ".exe"));
var tshockPath = "TShock.Server" + (isTarGz ? "" : ".exe");

if (!File.Exists(dotnetPath))
{
	try
	{
		Console.WriteLine("Extracting to ./dotnet/");
		if (isTarGz)
		{
			await using var archive = File.OpenRead(filename);
			await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
			TarFile.ExtractToDirectory(gzip, "dotnet", overwriteFiles: true);

			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(dotnetPath,
					UnixFileMode.UserRead |
					UnixFileMode.UserWrite |
					UnixFileMode.UserExecute |
					UnixFileMode.GroupRead |
					UnixFileMode.GroupExecute |
					UnixFileMode.OtherRead |
					UnixFileMode.OtherExecute);
			}
		}
		else
		{
			ZipFile.ExtractToDirectory(filename, "dotnet");
		}
	}
	catch (Exception ex)
	{
		Console.ForegroundColor = ConsoleColor.Red;
		Console.Error.WriteLine($"Failed to extract {filename}. The archive will be removed. Restart the installer to begin the download again.");
		Console.Error.WriteLine(ex);

		if (File.Exists(filename))
			File.Delete(filename);

		return;
	}
}
else
{
	Console.WriteLine($"Extract skipped, existing found at: {dotnetPath}");
}

var dotnetRoot = Path.GetFullPath("dotnet");
Console.WriteLine($"To be able to run {tshockPath} yourself set the environment variable DOTNET_ROOT={dotnetRoot}");

Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);

Console.WriteLine($"Extracted, launching: {tshockPath}");

var proc = new Process();

Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = !proc.HasExited;
};

proc.StartInfo = new()
{
	FileName = tshockPath,
};
foreach (var arg in args)
	proc.StartInfo.ArgumentList.Add(arg);
proc.Start();
await proc.WaitForExitAsync();
