﻿/*
 * (c) 2018 JetBrains s.r.o., SLaks, EditorConfig Team
 * Under MIT License
 * From https://github.com/editorconfig/editorconfig-core-net
 * From https://github.com/SLaks/Minimatch
 */

namespace EditorConfig.Core
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.Linq;
  using System.Text;
  using System.Text.RegularExpressions;

  // ReSharper disable UnusedAutoPropertyAccessor.Global

  ///<summary>Contains options that control how Minimatch matches strings.</summary>
  public class GlobMatcherOptions
  {
    ///<summary>If true, backslashes in paths will be treated as forward slashes.</summary>
    public bool AllowWindowsPaths { get; set; }

    ///<summary>If true, backslashes in patterns will be treated as forward slashes. This disables escape characters.</summary>
    public bool AllowWindowsPathsInPatterns { get; set; }

    /// <summary>
    ///   Allow patterns to match filenames starting with a period, even if the pattern does not explicitly have a period in
    ///   that spot.
    ///   Note that by default, <c>a/**/b</c>  will not match <c>a/.d/b</c>, unless dot is set.
    /// </summary>
    public bool Dot { get; set; }

    ///<summary>Returns from negate expressions the same as if they were not negated. (ie, true on a hit, false on a miss).</summary>
    public bool FlipNegate { get; set; }

    ///<summary>Ignores case differences when matching.</summary>
    public bool IgnoreCase { get; set; }

    /// <summary>
    ///   If set, then patterns without slashes will be matched against the basename of the path if it contains slashes.
    ///   For example, <c>a?b</c> would match the path <c>/xyz/123/acb</c>, but not <c>/xyz/acb/123</c>.
    /// </summary>
    public bool MatchBase { get; set; }

    ///<summary>Do not expand {a,b} and {1.3} brace sets.</summary>
    public bool NoBrace { get; set; }

    ///<summary>Suppresses the behavior of treating # at the start of a pattern as a comment.</summary>
    public bool NoComment { get; set; }

    ///<summary>Disable ** matching against multiple folder names.</summary>
    public bool NoGlobStar { get; set; }

    ///<summary>Suppresses the behavior of treating a leading ! character as negation.</summary>
    public bool NoNegate { get; set; }

    /// <summary>
    ///   When a match is not found by Match(), return a list containing the pattern itself. If not set, an empty list
    ///   is returned if there are no matches.
    /// </summary>
    public bool NoNull { get; set; }
  }

  // ReSharper restore UnusedAutoPropertyAccessor.Global

  public sealed class GlobMatcher
  {
    private static readonly char[] OurUnixPathSeparators = { '/' };

    private static readonly char[] OurWinPathSeparators = { '/', '\\' };

    private static readonly Regex OurHasBraces = new Regex(@"\{.*\}");

    private static readonly Regex OurNumericSet = new Regex(@"^\{(-?[0-9]+)\.\.(-?[0-9]+)\}");

    private readonly GlobMatcherOptions _myOptions;

    private readonly List<PatternCase>? _mySet;

    private readonly bool _myNegate;

    private readonly bool _myComment;

    private readonly bool _myEmpty;

    private GlobMatcher(GlobMatcherOptions options, List<PatternCase>? parsedPatternSet = null, bool negate = false, bool comment = false, bool empty = false)
    {
      _myOptions = options;
      _mySet = parsedPatternSet;
      _myNegate = negate;
      _myComment = comment;
      _myEmpty = empty;
    }

    private interface IPatternElement
    {
    }

    ///<summary>Creates a new GlobMatcher instance, parsing the pattern into a regex.</summary>
    public static GlobMatcher Create(string pattern, GlobMatcherOptions? options = null)
    {
      if (pattern == null)
      {
        throw new ArgumentNullException(nameof(pattern));
      }

      options ??= new GlobMatcherOptions();
      pattern = pattern.Trim();
      if (options.AllowWindowsPathsInPatterns)
      {
        pattern = pattern.Replace('\\', '/');
      }

      // empty patterns and comments match nothing.
      if (!options.NoComment && !string.IsNullOrEmpty(pattern) && pattern[0] == '#')
      {
        return new GlobMatcher(options, comment: true);
      }

      if (string.IsNullOrEmpty(pattern))
      {
        return new GlobMatcher(options, empty: true);
      }

      // step 1: figure out negation, etc.
      bool negate = ParseNegate(options, ref pattern);

      // step 2: expand braces
      var globSet = BraceExpand(pattern, options);

      // step 3: now we have a set, so turn each one into a series of path-portion
      // matching patterns.
      /*var list = new List<string>(globSet.Count);
      for (var index = 0; index < globSet.Count; index++)
      {
        var s = globSet[index];
        list.Add(ourSlashSplit.Split(s));
      }*/

      // glob --> regexps
      var list1 = new List<PatternCase>(globSet.Count);
      foreach (var g in globSet)
      {
        var parsedSet = Parse(options, g);
        if (parsedSet == null)
        {
          goto nextG;
        }

        list1.Add(parsedSet);

        nextG:;
      }

      return new GlobMatcher(options, list1, negate);
    }

    ///<summary>Checks whether a given string matches this pattern.</summary>
    public bool IsMatch(string input)
    {
      if (input is null)
      {
        throw new ArgumentNullException(nameof(input));
      }

      // short-circuit in the case of busted things.
      // comments, etc.
      if (_myComment)
      {
        return false;
      }

      if (_myEmpty)
      {
        return input.Length == 0;
      }

      // just ONE of the pattern sets in this.set needs to match
      // in order for it to be valid.  If negating, then just one
      // match means that we have failed.
      // Either way, return on the first hit.

      if (_mySet == null)
      {
        throw new InvalidOperationException();
      }

      foreach (var pattern in _mySet)
      {
        var hit = new MatchContext(_myOptions, input, pattern).MatchOne();
        if (!hit)
        {
          continue;
        }

        if (_myOptions.FlipNegate)
        {
          return true;
        }

        return !_myNegate;
      }

      // didn't get any hits.  this is success if it's a negative
      // pattern, failure otherwise.
      return !_myOptions.FlipNegate && _myNegate;
    }

    // Brace expansion:
    // a{b,c}d -> abd acd
    // a{b,}c -> abc ac
    // a{0..3}d -> a0d a1d a2d a3d
    // a{b,c{d,e}f}g -> abg acdfg acefg
    // a{b,c}d{e,f}g -> abdeg acdeg abdeg abdfg
    //
    // Invalid sets are not expanded.
    // a{2..}b -> a{2..}b
    // a{b}c -> a{b}c
    ///<summary>Expands all brace ranges in a pattern, returning a sequence containing every possible combination.</summary>
    private static IList<string> BraceExpand(string pattern, GlobMatcherOptions options)
    {
      if (options.NoBrace || !OurHasBraces.IsMatch(pattern))
      {
        // shortcut. no need to expand.
        return new[] { pattern };
      }

      bool escaping = false;
      int i;

      // examples and comments refer to this crazy pattern:
      // a{b,c{d,e},{f,g}h}x{y,z}
      // expected:
      // abxy
      // abxz
      // acdxy
      // acdxz
      // acexy
      // acexz
      // afhxy
      // afhxz
      // aghxy
      // aghxz

      // everything before the first \{ is just a prefix.
      // So, we pluck that off, and work with the rest,
      // and then prepend it to everything we find.
      if (pattern[0] != '{')
      {
        // console.error(pattern)
        string? prefix = null;
        for (i = 0; i < pattern.Length; i++)
        {
          var c = pattern[i];

          // console.error(i, c)
          if (c == '\\')
          {
            escaping = !escaping;
          }
          else if (c == '{' && !escaping)
          {
            prefix = pattern.Substring(0, i);
            break;
          }
        }

        // actually no sets, all { were escaped.
        if (prefix == null)
        {
          // console.error("no sets")
          return new[] { pattern };
        }

        var braceExpand = BraceExpand(pattern.Substring(i), options);

        for (var index = 0; index < braceExpand.Count; index++)
        {
          braceExpand[index] = prefix + braceExpand[index];
        }

        return braceExpand;
      }

      // now we have something like:
      // {b,c{d,e},{f,g}h}x{y,z}
      // walk through the set, expanding each part, until
      // the set ends.  then, we'll expand the suffix.
      // If the set only has a single member, then put the {} back

      // first, handle numeric sets, since they're easier
      var numset = OurNumericSet.Match(pattern);
      if (numset.Success)
      {
        // console.error("numset", numset[1], numset[2])
        var suf = BraceExpand(pattern.Substring(numset.Length), options).ToList();
        int start = int.Parse(numset.Groups[1].Value, CultureInfo.InvariantCulture),
            end = int.Parse(numset.Groups[2].Value, CultureInfo.InvariantCulture),
            inc = start > end ? -1 : 1;

        var retVal = new List<string>(Math.Abs(end + inc - start) * suf.Count);
        for (var w = start; w != (end + inc); w += inc)
        {
          // append all the suffixes
          foreach (var t in suf)
          {
            retVal.Add(w.ToString(CultureInfo.InvariantCulture) + t);
          }
        }

        return retVal;
      }

      // ok, walk through the set
      // We hope, somewhat optimistically, that there
      // will be a } at the end.
      // If the closing brace isn't found, then the pattern is
      // interpreted as braceExpand("\\" + pattern) so that
      // the leading \{ will be interpreted literally.
      int depth = 1;
      var set = new List<string>();
      string member = "";

      for (i = 1; i < pattern.Length && depth > 0; i++)
      {
        var c = pattern[i];

        // console.error("", i, c)

        if (escaping)
        {
          escaping = false;
          member += "\\" + c;
        }
        else
        {
          switch (c)
          {
            case '\\':
              escaping = true;
              continue;

            case '{':
              depth++;
              member += "{";
              continue;

            case '}':
              depth--;

              // if this closes the actual set, then we're done
              if (depth == 0)
              {
                set.Add(member);
                member = "";

                // pluck off the close-brace
                break;
              }
              else
              {
                member += c;
                continue;
              }

            case ',':
              if (depth == 1)
              {
                set.Add(member);
                member = "";
              }
              else
              {
                member += c;
              }

              continue;

            default:
              member += c;
              continue;
          } // switch
        } // else
      } // for

      // now we've either finished the set, and the suffix is
      // pattern.substr(i), or we have *not* closed the set,
      // and need to escape the leading brace
      if (depth != 0)
      {
        // console.error("didn't close", pattern)
        return BraceExpand("\\" + pattern, options);
      }

      // ["b", "c{d,e}","{f,g}h"] ->
      //   ["b", "cd", "ce", "fh", "gh"]
      var addBraces = set.Count == 1;

      var set1 = new List<string>(set.Count);
      foreach (string p in set)
      {
        set1.AddRange(BraceExpand(p, options));
      }

      set = set1;

      if (addBraces)
      {
        for (var index = 0; index < set.Count; index++)
        {
          set[index] = "{" + set[index] + "}";
        }
      }

      // now attach the suffixes.
      // x{y,z} -> ["xy", "xz"]
      // console.error("set", set)
      // console.error("suffix", pattern.substr(i))
      var s2 = BraceExpand(pattern.Substring(i), options);
      var list1 = new List<string>(s2.Count * set.Count);
      list1.AddRange(
                     from s1 in s2
                     from s in set
                     select s + s1);

      return list1;
    }

    private static bool IsPathSeparator(GlobMatcherOptions options, char c)
    {
      // windows: need to use /, not \
      // On other platforms, \ is a valid (albeit bad) filename char.
      return c == '/' || (options.AllowWindowsPaths && c == '\\');
    }

    // parse a component of the expanded set.
    // ReSharper disable once FunctionComplexityOverflow
    private static PatternCase Parse(GlobMatcherOptions options, string? pattern)
    {
      if (pattern?.Length == 0)
      {
        return new PatternCase();
      }

      var result = new PatternCase();
      var sb = new StringBuilder();

      bool escaping = false, inClass = false, negate = false, range = false;
      int classStart = -1;

      void FinishLiteral()
      {
        Debug.Assert(!escaping && !inClass, "!escaping && !inClass");
        if (sb.Length <= 0)
        {
          return;
        }

        result.Add(new Literal(sb.ToString()));
        sb.Clear();
      }

      void AppendChar(char c1)
      {
        if (inClass && range)
        {
          char firstChar = sb[sb.Length - 1];
          firstChar++;

          for (char c2 = firstChar; c2 <= c1; c2++)
          {
            sb.Append(c2);
          }

          range = false;
        }
        else
        {
          sb.Append(c1);
        }
      }

      if (pattern == null)
      {
        throw new InvalidOperationException();
      }

      for (var i = 0; i < pattern.Length; i++)
      {
        var c = pattern[i];

        // skip over any that are escaped.
        if (escaping && c != '/')
        {
          AppendChar(c);
          escaping = false;
        }
        else
        {
          switch (c)
          {
            case '/':
              if (inClass)
              {
                // Class is left open
                HandleOpenClass();
                continue;
              }
              else
              {
                if (escaping)
                {
                  sb.Append('\\');
                  escaping = false;
                }

                FinishLiteral();

                if (!(result.LastOrDefault() is PathSeparator))
                {
                  result.Add(PathSeparator.Instance);
                }
              }

              break;

            case '\\':
              escaping = true;
              break;

            case '!':
            case '^':
              if (inClass && i == classStart + 1)
              {
                // the glob [!a] means negation
                negate = true;
              }
              else
              {
                AppendChar(c);
              }

              break;

            case '?':
              if (inClass)
              {
                AppendChar(c);
              }
              else
              {
                FinishLiteral();
                result.Add(OneChar.EmptyInstance);
              }

              break;

            case '*':
              if (inClass)
              {
                AppendChar(c);
              }
              else
              {
                FinishLiteral();
                if (result.LastOrDefault() is Asterisk && !options.NoGlobStar)
                {
                  result.RemoveAt(result.Count - 1);
                  result.Add(new DoubleAsterisk());
                }
                else if (!(result.LastOrDefault() is SimpleAsterisk))
                {
                  result.Add(new SimpleAsterisk());
                }
              }

              break;

            // these are mostly the same in regexp and glob
            case '[':

              if (inClass)
              {
                AppendChar(c);
              }
              else
              {
                FinishLiteral();
                inClass = true;
                negate = false;
                range = false;
                classStart = i;
              }

              break;

            case ']':
              //  a right bracket shall lose its special
              //  meaning and represent itself in
              //  a bracket expression if it occurs
              //  first in the list.  -- POSIX.2 2.8.3.2
              if (i == classStart + 1 || (negate && i == classStart + 2) || !inClass)
              {
                AppendChar(c);
              }
              else
              {
                if (range)
                {
                  sb.Append('-');
                }

                // finish up the class.
                inClass = false;
                result.Add(new OneChar(sb.ToString(), negate));
                sb.Clear();
              }

              break;

            case '-':
              if (i == classStart + 1 || (negate && i == classStart + 2) || !inClass || range)
              {
                AppendChar(c);
              }
              else
              {
                range = true;
              }

              break;

            default:
              AppendChar(c);
              break;
          } // switch
        } // if

        if (i == pattern.Length - 1)
        {
          if (inClass)
          {
            HandleOpenClass();

            // Do not continue, because next check could be relevant
          }
        }

        if (i == pattern.Length - 1)
        {
          if (escaping)
          {
            sb.Append('\\');
            escaping = false;
            FinishLiteral();
          }
          else
          {
            FinishLiteral();
          }
        }

        void HandleOpenClass()
        {
          // handle the case where we left a class open.
          // "[abc" is valid, equivalent to "\[abc"

          // split where the last [ was, and escape it
          // this is a huge pita.  We now have to re-walk
          // the contents of the would-be class to re-translate
          // any characters that were passed through as-is

          sb.Clear();
          if (result.LastOrDefault() is Literal literal)
          {
            sb.Append(literal.Source);
            result.RemoveAt(result.Count - 1);
          }

          sb.Append('[');

          escaping = false;
          i = classStart;
          inClass = false;
        } // Handle open class
      } // for

      result.Build();
      return result;
    }

    private static bool ParseNegate(GlobMatcherOptions options, ref string pattern)
    {
      var negateOffset = 0;

      if (options.NoNegate)
      {
        return false;
      }

      bool negate = false;

      for (var i = 0; i < pattern.Length && pattern[i] == '!'; i++)
      {
        negate = !negate;
        negateOffset++;
      }

      if (negateOffset > 0)
      {
        pattern = pattern.Substring(negateOffset);
      }

      return negate;
    }

    private struct MatchContext
    {
      private readonly GlobMatcherOptions _myOptions;

      private readonly PatternCase _myPatternCase;

      private readonly string _myStr;

      private int _myStartOffset;

      private int _myEndOffset;

      private int _myStartItem;

      private int _myEndItem;

      private int _myLastAsteriskItem;

      private int _myNextPositionForAsterisk;

      private StringComparison ComparisonType => _myOptions.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

      private char[] PathSeparatorChars => _myOptions.AllowWindowsPaths ? OurWinPathSeparators : OurUnixPathSeparators;

      public MatchContext(GlobMatcherOptions options, string str, PatternCase patternCase)
      {
        _myOptions = options;
        _myStr = str;
        _myPatternCase = patternCase;
        _myStartOffset = 0;
        _myEndOffset = _myStr.Length;
        _myStartItem = 0;
        _myEndItem = _myPatternCase.Count - 1;
        _myLastAsteriskItem = -1;
        _myNextPositionForAsterisk = -1;
      }

      public bool MatchOne()
      {
        if (_myOptions.MatchBase && !_myPatternCase.HasPathSeparators)
        {
          SkipLastPathSeparators();

          int lastSeparator = _myStr.LastIndexOfAny(PathSeparatorChars, _myEndOffset - 1, _myEndOffset - _myStartOffset);
          if (lastSeparator != -1)
          {
            _myStartOffset = lastSeparator + 1;
          }
        }

        var oldEndItem = _myEndItem;
        var oldEndOffset = _myEndOffset;
        if (MatchOneBackwards())
        {
          return MatchOneForward();
        }

        _myEndItem = oldEndItem;
        _myEndOffset = oldEndOffset;

        // file a/b/ should match pattern a/b, so let's try again without trailing /
        if (!SkipLastPathSeparators())
        {
          return false;
        }

        return MatchOneBackwards() && MatchOneForward();
      }

      private bool MatchOneBackwards()
      {
        while (_myStartItem <= _myEndItem)
        {
          switch (_myPatternCase[_myEndItem])
          {
            case Asterisk _:
              return true;

            case Literal literal:
              if (_myEndOffset - _myStartOffset < literal.Source.Length)
              {
                return false; // Not enough chars
              }

              int pos = _myStr.LastIndexOf(literal.Source, _myEndOffset - 1, literal.Source.Length, ComparisonType);
              if (pos == -1)
              {
                return false;
              }

              _myEndOffset = pos;
              break;

            case PathSeparator _:
              if (_myStartItem <= _myEndItem - 1)
              {
                // If we have pattern like a/**/b, then it should be matched by a/b, so don't eat path separator after **
                if (_myPatternCase[_myEndItem - 1] is DoubleAsterisk)
                {
                  return true;
                }
              }

              if (_myEndOffset - _myStartOffset < 1)
              {
                return false; // Not enough chars
              }

              if (!IsPathSeparator(_myOptions, _myStr[_myEndOffset - 1]))
              {
                return false;
              }

              do
              {
                _myEndOffset--;
                if (_myEndOffset - _myStartOffset < 1)
                {
                  break;
                }
              }
              while (IsPathSeparator(_myOptions, _myStr[_myEndOffset - 1]));

              break;

            case OneChar oneCharParseItem:
              if (_myEndOffset - _myStartOffset < 1)
              {
                return false; // Not enough chars
              }

              if (!oneCharParseItem.CheckChar(_myOptions, _myStr[_myEndOffset - 1], ComparisonType))
              {
                return false;
              }

              _myEndOffset--;
              break;

            default:
              Debug.Fail("Unknown item");
              break;
          }

          _myEndItem--;
        }

        // if chars remain, but no pattern items - false
        return _myEndOffset - _myStartOffset <= 0;
      }

      private bool SkipLastPathSeparators()
      {
        bool success = false;
        while (_myEndOffset - _myStartOffset > 0 && IsPathSeparator(_myOptions, _myStr[_myEndOffset - 1]))
        {
          _myEndOffset--;
          success = true;
        }

        return success;
      }

      private bool MatchOneForward()
      {
        while (_myStartItem <= _myEndItem)
        {
          var item = _myPatternCase[_myStartItem];

          switch (item)
          {
            case Asterisk asterisk:
              if (_myStartItem == _myEndItem)
              {
                return CheckMatchedByAsterisk(asterisk, true, _myStartOffset, _myEndOffset); // Last asterisk just matches everything
              }

              // Suppose we have two asterisks in a pattern, so pattern looks like this:
              // <part1>*<part2>*<part3>
              // where <part2> doesn't contain asterisks,
              // and we have a string with several possible matches for <part2>, like this:
              // <MatchingPart1><Something1><MatchingPart2_a><Something2><MatchingPart2_b><Something3><MatchingPart3>.

              // If we can match <part2> to <MatchingPart2_b> so that the whole string matches to pattern, with
              // asterisk1 matching to <Something1><MatchingPart2_a><Something2>
              // and asterisk2 matching to <Something3>,
              // then we can also match the string to pattern with <part2> matching to <MatchingPart2_a>, because we can
              // match asterisk1 to <Something1>
              // and asterisk2 to <Something2><MatchingPart2_b><Something3>.

              // Because of this we can just take a first match for pattern part between two asterisks, and
              // don't need to iterate any further recursively.

              // The same works for two globstars (**) and for * and **. But it doesn't work between ** and *, because
              // if <MatchingPart2_a> or <Something2> contains slashes, then we cannot move then to match *.
              // So in that case we need to make recursion.

              _myLastAsteriskItem = _myStartItem;

              if (!(item is DoubleAsterisk) || !(asterisk.NextAsterisk is SimpleAsterisk))
              {
                if (!GotoNextPositionForAsterisk(true))
                {
                  return false;
                }

                break;
              }

              // We have to use recursion here, see discussion above

              // Take the rest of the pattern after
              // the **, and see if it would match the file remainder.
              // If so, return success.
              // If not, the ** "swallows" a segment, and try again.
              // This is recursively awful.
              //
              // a/**/*test*/**/*.cs matching a/b/c/mytestfolder/d/e.cs should go like this
              // - a matches a
              // - doublestar followed by *
              //       - matchOne(b/c/mytestfolder/d/e.cs, *test*/**/*.cs) -> no
              //       - matchOne(/c/mytestfolder/d/e.cs, /*test*/**/*.cs) -> no
              //       - matchOne(/mytestfolder/d/e.cs, /*test*/**/*.cs)
              //             - /mytestfolder/ matches /*test*/
              //             - doublestar followed by *
              //                     - matchOne(d/e.cs, *.cs) -> no
              //                     - matchOne(/e.cs, /*.cs) -> yes, hit
              bool first = true;
              var oldLastAsteriskItem = _myLastAsteriskItem;
              while (true)
              {
                _myStartItem = _myLastAsteriskItem + 1;
                if (!GotoNextPositionForAsterisk(first))
                {
                  return false;
                }

                var oldNextPositionForAsterisk = _myNextPositionForAsterisk;
                _myLastAsteriskItem = -1;
                _myNextPositionForAsterisk = -1;
                if (MatchOneForward())
                {
                  return true;
                }

                _myLastAsteriskItem = oldLastAsteriskItem;
                _myNextPositionForAsterisk = oldNextPositionForAsterisk;

                _myStartOffset = _myNextPositionForAsterisk;
                first = false;
              }

            case PathSeparator _:
              if (_myEndOffset - _myStartOffset < 1)
              {
                return false; // Not enough chars
              }

              if (!IsPathSeparator(_myOptions, _myStr[_myStartOffset]))
              {
                goto Mismatch;
              }

              do
              {
                _myStartOffset++;
                if (_myEndOffset - _myStartOffset < 1)
                {
                  break;
                }
              }
              while (IsPathSeparator(_myOptions, _myStr[_myStartOffset]));

              break;

            case Literal literal:
              if (_myEndOffset - _myStartOffset < literal.Source.Length)
              {
                return false; // Not enough chars
              }

              int pos = _myStr.IndexOf(literal.Source, _myStartOffset, literal.Source.Length, ComparisonType);
              if (pos == -1)
              {
                goto Mismatch;
              }

              _myStartOffset = pos + literal.Source.Length;
              break;

            case OneChar oneChar:
              if (_myEndOffset - _myStartOffset < 1)
              {
                return false; // Not enough chars
              }

              char c = _myStr[_myStartOffset];
              if (!oneChar.CheckChar(_myOptions, c, ComparisonType))
              {
                goto Mismatch;
              }

              if (c == '.' && !CheckDot(_myStartOffset))
              {
                goto Mismatch;
              }

              _myStartOffset++;
              break;

            default:
              Debug.Fail("Unknown item");
              break;
          }

          _myStartItem++;

          if (_myStartItem > _myEndItem && _myEndOffset - _myStartOffset > 0)
          {
            if (_myEndOffset == _myStr.Length)
            {
              // ran out of pattern, still have file left.
              // this is only acceptable if we're on the very last
              // empty segment of a file with a trailing slash.
              // a/* should match a/b/

              SkipLastPathSeparators();
            }

            if (_myEndOffset - _myStartOffset > 0)
            {
              goto Mismatch;
            }

            return true;
          }

          continue;

          Mismatch:
          if (_myLastAsteriskItem == -1)
          {
            return false;
          }

          _myStartItem = _myLastAsteriskItem + 1;
          _myStartOffset = _myNextPositionForAsterisk;

          if (!GotoNextPositionForAsterisk(false))
          {
            return false;
          }
        }

        return _myEndOffset - _myStartOffset <= 0;
      }

      private bool GotoNextPositionForAsterisk(bool first)
      {
        Debug.Assert(_myLastAsteriskItem >= 0 && _myLastAsteriskItem < _myPatternCase.Count, "lastAsteriskItem >= 0 && lastAsteriskItem < patternCase.Count");
        var asterisk = (Asterisk)_myPatternCase[_myLastAsteriskItem];
        int fixedItemsLengthAfterAsterisk = asterisk.FixedItemsLengthAfterAsterisk;
        if (_myEndOffset - _myStartOffset < fixedItemsLengthAfterAsterisk)
        {
          return false;
        }

        var oldStartPos = _myStartOffset;

        int literalAfterAsterisk = asterisk.LiteralAfterAsterisk;
        if (literalAfterAsterisk != -1)
        {
          int numberOfOneCharItemsBefore = literalAfterAsterisk - _myLastAsteriskItem - 1;
          if (_myPatternCase[literalAfterAsterisk] is Literal literal)
          {
            int pos = _myStr.IndexOf(literal.Source, _myStartOffset + numberOfOneCharItemsBefore, _myEndOffset - _myStartOffset - numberOfOneCharItemsBefore, ComparisonType);
            if (pos == -1)
            {
              return false;
            }

            _myStartOffset = pos - numberOfOneCharItemsBefore;
            if (_myEndOffset - _myStartOffset < fixedItemsLengthAfterAsterisk)
            {
              return false;
            }
          }
          else
          {
            Debug.Assert(_myPatternCase[literalAfterAsterisk] is PathSeparator, "parseItems[literalAfterAsteriskItem] is PathSeparatorParseItem");

            if (first && asterisk is DoubleAsterisk && literalAfterAsterisk == _myLastAsteriskItem + 1 &&
                (_myLastAsteriskItem == 0 || _myPatternCase[_myLastAsteriskItem - 1] is PathSeparator))
            {
              // If we have pattern like a/**/b or **/a, then it should be matched by a/b and a
              _myStartItem++;
              _myNextPositionForAsterisk = _myStartOffset;
              return true;
            }

            int pos = _myStr.IndexOfAny(PathSeparatorChars, _myStartOffset + numberOfOneCharItemsBefore, _myEndOffset - _myStartOffset - numberOfOneCharItemsBefore);
            if (pos == -1)
            {
              return false;
            }

            _myStartOffset = pos - numberOfOneCharItemsBefore;
            if (_myEndOffset - _myStartOffset < fixedItemsLengthAfterAsterisk)
            {
              return false;
            }
          }
        }

        int newStartPos = _myStartOffset;

        if (!CheckMatchedByAsterisk(asterisk, first, oldStartPos, newStartPos))
        {
          return false;
        }

        _myNextPositionForAsterisk = _myStartOffset + 1;
        return true;
      }

      private bool CheckMatchedByAsterisk(Asterisk asteriskItem, bool first, int oldStartPos, int newStartPos)
      {
        if (asteriskItem is SimpleAsterisk)
        {
          if (first && newStartPos == oldStartPos)
          {
            // a/b/ should *not* match "a/b/*"

            bool atStart = newStartPos == 0 || IsPathSeparator(_myOptions, _myStr[newStartPos - 1]);
            bool atEnd = newStartPos == _myStr.Length || IsPathSeparator(_myOptions, _myStr[newStartPos]);
            if (atStart && atEnd)
            {
              return false;
            }
          }

          if (newStartPos > oldStartPos)
          {
            if (_myStr.IndexOfAny(PathSeparatorChars, oldStartPos, newStartPos - oldStartPos) != -1)
            {
              return false;
            }

            if (first && _myStr[oldStartPos] == '.' && !CheckDot(oldStartPos))
            {
              return false;
            }
          }
        }

        if (!(asteriskItem is DoubleAsterisk) || newStartPos <= oldStartPos)
        {
          return true;
        }

        int length = newStartPos - oldStartPos;

        if (newStartPos < _myStr.Length)
        {
          // We also search for dot immediately after **. For example, pattern **.hidden shouldn't be matched by **/.hidden 
          length++;
        }

        var dotPos = _myStr.IndexOf('.', oldStartPos, length);
        return dotPos == -1 || CheckDot(dotPos);
      }

      private bool CheckDot(int dotPos)
      {
        // .x should not match neither *x, nor **x, nor ?x, unless
        // myOptions.Dot is set.
        // . and .. are *never* matched by *, ** or ?, for explosively
        // exponential reasons.
        // also **.* should not match /., /.. and /.x (unless myOptions.Dot is set)

        if (dotPos != 0 && !IsPathSeparator(_myOptions, _myStr[dotPos - 1]))
        {
          return true; // ok, dot is in the middle of file/directory name
        }

        if (!_myOptions.Dot)
        {
          return false; // implicit dot at the beginning is prohibited
        }

        if (dotPos == _myStr.Length - 1)
        {
          return false; // file name is ".", prohibited
        }

        if (IsPathSeparator(_myOptions, _myStr[dotPos + 1]))
        {
          return false; // directory name is ".", prohibited
        }

        if (_myStr[dotPos + 1] != '.')
        {
          return true; // it's not "..", allow this
        }

        if (dotPos + 1 == _myStr.Length - 1)
        {
          return false; // file name is "..", prohibited
        }

        // false: directory name is "..", prohibited
        // true: just a file/directory name that starts with "..", allow this
        return !IsPathSeparator(_myOptions, _myStr[dotPos + 2]);
      }
    }

    private abstract class Asterisk : IPatternElement
    {
      public int FixedItemsLengthAfterAsterisk { get; set; }

      public int LiteralAfterAsterisk { get; set; } = -1;

      public Asterisk? NextAsterisk { get; set; }
    }

    private class DoubleAsterisk : Asterisk
    {
    }

    private class Literal : IPatternElement
    {
      public Literal(string source)
      {
        Source = source;
      }

      public string Source { get; }
    }

    private class OneChar : IPatternElement
    {
      public static readonly OneChar EmptyInstance = new OneChar(null, false);

      static OneChar()
      {
      }

      public OneChar(string? possibleChars, bool negate)
      {
        PossibleChars = possibleChars;
        Negate = negate;
      }

      private bool Negate { get; }

      private string? PossibleChars { get; }

      public bool CheckChar(GlobMatcherOptions options, char c, StringComparison comparison)
      {
        if (IsPathSeparator(options, c))
        {
          return false;
        }

        if (PossibleChars != null)
        {
          return (PossibleChars.IndexOf(c.ToString(CultureInfo.InvariantCulture), comparison) != -1) != Negate;
        }

        return true;
      }
    }

    private class PathSeparator : IPatternElement
    {
      public static readonly PathSeparator Instance = new PathSeparator();

      static PathSeparator()
      {
      }

      private PathSeparator()
      {
      }
    }

    private class PatternCase : List<IPatternElement>
    {
      public bool HasPathSeparators { get; private set; }

      public void Build()
      {
        HasPathSeparators = false;
        Asterisk? lastAsterisk = null;
        int fixedItemsLength = 0;
        for (int i = 0; i < Count; i++)
        {
          var item = this[i];
          if (item is PathSeparator || item is DoubleAsterisk)
          {
            HasPathSeparators = true;
          }

          switch (item)
          {
            case Literal literal:
              if (lastAsterisk?.LiteralAfterAsterisk == -1)
              {
                lastAsterisk.LiteralAfterAsterisk = i;
              }

              fixedItemsLength += literal.Source.Length;
              break;

            case PathSeparator _:
              if (lastAsterisk?.LiteralAfterAsterisk == -1)
              {
                lastAsterisk.LiteralAfterAsterisk = i;
              }

              // First slash after ** could be skipped
              if (!(lastAsterisk is DoubleAsterisk) || fixedItemsLength > 0)
              {
                fixedItemsLength++;
              }

              break;

            case OneChar _:
              fixedItemsLength++;
              break;

            case Asterisk item1:
              if (lastAsterisk != null)
              {
                lastAsterisk.NextAsterisk = item1;
                lastAsterisk.FixedItemsLengthAfterAsterisk = fixedItemsLength;
              }

              fixedItemsLength = 0;
              lastAsterisk = item1;
              break;
          }
        }
      }
    }

    private class SimpleAsterisk : Asterisk
    {
    }
  }
}