﻿namespace EditorConfig.App
{
	using System;
	using System.Linq;

	internal class ArgumentsParser
	{
		public string[] FileNames { get; } =
#if NETCOREAPP
			Array.Empty<string>();
#else
			new string[0];
#endif
		public string ConfigFileName { get; } = string.Empty;
		public Version? DevelopVersion { get; }

		public bool PrintHelp { get; }
		public bool PrintVersion { get; }

		public ArgumentsParser(string[] args)
		{
			if (args.Length == 0)
			{
				throw new ApplicationArgumentException("Must specify atleast one FILEPATH");
			}

			while (args.Length > 0 && args[0].StartsWith("-", StringComparison.Ordinal))
			{
				switch (args[0])
				{
					case "-h":
					case "--help":
						PrintHelp = true;
						return;
					case "-v":
					case "--version":
						PrintVersion = true;
						return;
					case "-f":
						if (args.Length == 1 || args[1].StartsWith("-", StringComparison.Ordinal))
						{
							throw new ApplicationArgumentException("Option '-f' needs argument <path>");
						}

						ConfigFileName = args[1];
						args = args.Skip(2).ToArray();
						break;
					case "-b":
						if (args.Length == 1 || args[1].StartsWith("-", StringComparison.Ordinal))
						{
							throw new ApplicationArgumentException("Option '-b' needs argument <version>");
						}

						if (!Version.TryParse(args[1], out var version))
						{
							throw new ApplicationArgumentException("Option '-b' argument '{0}' is not valid version", args[1]);
						}

						DevelopVersion = version;
						args = args.Skip(2).ToArray();
						break;
					default:
						throw new ApplicationArgumentException("Unknown option '{0}'", args[0]);
				}
			}
			if (args.Length == 0)
			{
				throw new ApplicationArgumentException("You need to specify atleast one file");
			}

			FileNames = args;
		}
	}
}
