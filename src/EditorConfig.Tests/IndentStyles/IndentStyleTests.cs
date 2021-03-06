﻿namespace EditorConfig.Tests.IndentStyles
{
	using System.Reflection;

	using EditorConfig.Core;

	using FluentAssertions;

	using NUnit.Framework;

	[TestFixture]
	public class IndentStyleTests : EditorConfigTestBase
	{
		[Test]
		public void Space()
		{
			var file = GetConfig(MethodBase.GetCurrentMethod(), "f.x", ".space.editorconfig");
			file.IndentStyle.Should().Be(IndentStyle.Space);
		}

		[Test]
		public void Tab()
		{
			var file = GetConfig(MethodBase.GetCurrentMethod(), "f.x", ".tab.editorconfig");
			file.IndentStyle.Should().Be(IndentStyle.Tab);

			// Set indent_size to "tab" if indent_size is unspecified and indent_style is set to "tab".
			file.Properties.Should().HaveCount(2);
			file.IndentSize.Should().NotBeNull();
#pragma warning disable CS8602 // Dereference of a possibly null reference.
			file.IndentSize.UseTabWidth.Should().BeTrue();
#pragma warning restore CS8602 // Dereference of a possibly null reference.
		}

		[Test]
		public void Bogus()
		{
			var file = GetConfig(MethodBase.GetCurrentMethod(), "f.x", ".bogus.editorconfig");
			file.IndentStyle.Should().BeNull();
			HasBogusKey(file, "indent_style");
		}
	}
}
