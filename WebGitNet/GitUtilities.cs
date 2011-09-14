﻿//-----------------------------------------------------------------------
// <copyright file="GitUtilities.cs" company="(none)">
//  Copyright © 2011 John Gietzen. All rights reserved.
// </copyright>
// <author>John Gietzen</author>
//-----------------------------------------------------------------------

namespace WebGitNet
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Web.Configuration;
    using WebGitNet.Models;
    using System.Threading;
    using System.Net.Mail;

    public enum RefValidationResult
    {
        Valid,
        Invalid,
        Ambiguous,
    }

    public static class GitUtilities
    {
        public static Encoding DefaultEncoding
        {
            get { return Encoding.GetEncoding(28591); }
        }

        /// <summary>
        /// Quotes and Escapes a command-line argument for Git and Bash.
        /// </summary>
        private static string Q(string argument)
        {
            var result = new StringBuilder(argument.Length + 10);
            result.Append("\"");
            for (int i = 0; i < argument.Length; i++)
            {
                var ch = argument[i];
                switch (ch)
                {
                    case '\\':
                    case '\"':
                        result.Append('\\');
                        result.Append(ch);
                        break;

                    default:
                        result.Append(ch);
                        break;
                }
            }
            result.Append("\"");
            return result.ToString();
        }

        public static string Execute(string command, string workingDir, Encoding outputEncoding = null, bool trustErrorCode = false)
        {
            using (MvcMiniProfiler.MiniProfiler.StepStatic("Run: git " + command))
            {
                using (var git = Start(command, workingDir, redirectInput: false, redirectError: trustErrorCode, outputEncoding: outputEncoding))
                {
                    string error = null;
                    Thread errorThread = null;
                    if (trustErrorCode)
                    {
                        errorThread = new Thread(() => { error = git.StandardError.ReadToEnd(); });
                        errorThread.Start();
                    }

                    var result = git.StandardOutput.ReadToEnd();
                    git.WaitForExit();

                    if (trustErrorCode && git.ExitCode != 0)
                    {
                        errorThread.Join();
                        throw new GitErrorException(command, git.ExitCode, error);
                    }

                    return result;
                }
            }
        }

        public static Process Start(string command, string workingDir, bool redirectInput = false, bool redirectError = false, Encoding outputEncoding = null)
        {
            var git = WebConfigurationManager.AppSettings["GitCommand"];
            var startInfo = new ProcessStartInfo(git, command)
            {
                WorkingDirectory = workingDir,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = redirectError,
                StandardOutputEncoding = outputEncoding ?? DefaultEncoding,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            return Process.Start(startInfo);
        }

        public static void UpdateServerInfo(string repoPath)
        {
            Execute("update-server-info", repoPath);
        }

        public static List<GitRef> GetAllRefs(string repoPath)
        {
            var result = Execute("show-ref", repoPath);
            return (from l in result.Split("\n".ToArray(), StringSplitOptions.RemoveEmptyEntries)
                    let parts = l.Split(' ')
                    select new GitRef(parts[0], parts[1])).ToList();
        }

        public static RefValidationResult ValidateRef(string repoPath, string refName)
        {
            if (refName == "HEAD")
            {
                return RefValidationResult.Valid;
            }

            if (string.IsNullOrWhiteSpace(refName))
            {
                return RefValidationResult.Invalid;
            }

            String results;
            int exitCode;

            using (var git = Start(string.Format("show-ref --heads --tags -- {0}", Q(refName)), repoPath))
            {
                results = git.StandardOutput.ReadToEnd();
                git.WaitForExit();
                exitCode = git.ExitCode;
            }

            if (exitCode != 0)
            {
                return RefValidationResult.Invalid;
            }

            if (results.TrimEnd('\n').IndexOf('\n') >= 0)
            {
                return RefValidationResult.Ambiguous;
            }

            return RefValidationResult.Valid;
        }

        public static int CountCommits(string repoPath, string @object = null)
        {
            @object = @object ?? "HEAD";
            var results = Execute(string.Format("shortlog -s {0}", Q(@object)), repoPath);
            return (from r in results.Split("\n".ToArray(), StringSplitOptions.RemoveEmptyEntries)
                    let count = r.Split("\t".ToArray(), StringSplitOptions.RemoveEmptyEntries)[0]
                    select int.Parse(count.Trim())).Sum();
        }

        public static List<LogEntry> GetLogEntries(string repoPath, int count, int skip = 0, string @object = null)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            if (skip < 0)
            {
                throw new ArgumentOutOfRangeException("skip");
            }

            @object = @object ?? "HEAD";
            var results = Execute(string.Format("log -n {0} --encoding=UTF-8 -z --format=\"format:commit %H%ntree %T%nparent %P%nauthor %an%nauthor mail %ae%nauthor date %aD%ncommitter %cn%ncommitter mail %ce%ncommitter date %cD%nsubject %s%n%b%x00\" {1}", count + skip, Q(@object)), repoPath, Encoding.UTF8);

            Func<string, LogEntry> parseResults = result =>
            {
                var commit = ParseResultLine("commit ", result, out result);
                var tree = ParseResultLine("tree ", result, out result);
                var parent = ParseResultLine("parent ", result, out result);
                var author = ParseResultLine("author ", result, out result);
                var authorEmail = ParseResultLine("author mail ", result, out result);
                var authorDate = ParseResultLine("author date ", result, out result);
                var committer = ParseResultLine("committer ", result, out result);
                var committerEmail = ParseResultLine("committer mail ", result, out result);
                var committerDate = ParseResultLine("committer date ", result, out result);
                var subject = ParseResultLine("subject ", result, out result);
                var body = result;

                return new LogEntry(commit, tree, parent, author, authorEmail, authorDate, committer, committerEmail, committerDate, subject, body);
            };

            return (from r in results.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).Skip(skip)
                    select parseResults(r)).ToList();
        }

        private class Author
        {
            public string Name { get; set; }
            public string Email { get; set; }
        }

        private static Author Rename(Author author, IList<RenameEntry> entries)
        {
            Func<RenameField, Author, string> getField = (f, a) =>
            {
                if (f == RenameField.Name) return a.Name;
                if (f == RenameField.Email) return a.Email;
                return null;
            };

            Action<RenameField, Author, string> setField = (f, a, v) =>
            {
                if (f == RenameField.Name) a.Name = v;
                if (f == RenameField.Email) a.Email = v;
                return;
            };

            author = new Author { Name = author.Name, Email = author.Email };

            foreach (var entry in entries)
            {
                switch (entry.RenameStyle)
                {
                    case RenameStyle.Exact:
                        if (getField(entry.SourceField, author) == entry.Match)
                        {
                            foreach (var dest in entry.Destinations)
                            {
                                setField(dest.Field, author, dest.Replacement);
                            }
                        }
                        break;

                    case RenameStyle.CaseInsensitive:
                        if (entry.Match.Equals(getField(entry.SourceField, author), StringComparison.CurrentCultureIgnoreCase))
                        {
                            foreach (var dest in entry.Destinations)
                            {
                                setField(dest.Field, author, dest.Replacement);
                            }
                        }
                        break;

                    case RenameStyle.Regex:
                        if (Regex.IsMatch(getField(entry.SourceField, author), entry.Match))
                        {
                            var newAuthor = new Author { Name = author.Name, Email = author.Email };
                            foreach (var dest in entry.Destinations)
                            {
                                setField(dest.Field, newAuthor, Regex.Replace(getField(entry.SourceField, author), entry.Match, dest.Replacement));
                            }
                            author = newAuthor;
                        }
                        break;
                }
            }

            return author;
        }

        public static List<UserImpact> GetUserImpacts(string repoPath)
        {
            List<RenameEntry> renames = new List<RenameEntry>();
            List<IgnoreEntry> ignores = new List<IgnoreEntry>();

            var parentRenames = Path.Combine(new DirectoryInfo(repoPath).Parent.FullName, "renames");
            var renamesFile = Path.Combine(repoPath, "info", "webgit.net", "renames");
            var ignoresFile = Path.Combine(repoPath, "info", "webgit.net", "ignore");

            Action<string> readRenames = (file) =>
            {
                if (File.Exists(file))
                {
                    renames.AddRange(RenameFileParser.Parse(File.ReadAllLines(file)));
                }
            };

            readRenames(parentRenames);
            readRenames(renamesFile);

            if (File.Exists(ignoresFile))
            {
                ignores.AddRange(IgnoreFileParser.Parse(File.ReadAllLines(ignoresFile)));
            }

            string impactData;
            using (var git = Start("log -z --format=%x01%H%x1e%ai%x1e%ae%x1e%an%x02 --numstat", repoPath, outputEncoding: Encoding.UTF8))
            {
                impactData = git.StandardOutput.ReadToEnd();
            }

            var individualImpacts = from imp in impactData.Split("\x01".ToArray(), StringSplitOptions.RemoveEmptyEntries)
                                    select ParseUserImpact(imp, renames, ignores);

            return
                individualImpacts
                .GroupBy(i => i.Author, StringComparer.InvariantCultureIgnoreCase)
                .Select(g => new UserImpact
                {
                    Author = g.Key,
                    Commits = g.Sum(ui => ui.Commits),
                    Insertions = g.Sum(ui => ui.Insertions),
                    Deletions = g.Sum(ui => ui.Deletions),
                    Impact = g.Sum(ui => ui.Impact),
                })
                .OrderByDescending(i => i.Commits)
                .ToList();
        }

        private static UserImpact ParseUserImpact(string impactData, IList<RenameEntry> renames, IList<IgnoreEntry> ignores)
        {
            var impactParts = impactData.Split("\x02".ToArray(), 2);
            var header = impactParts[0];
            var body = impactParts[1].TrimStart('\n');

            var headerParts = header.Split("\x1e".ToArray(), 4);
            var hash = headerParts[0];
            var date = headerParts[1];
            var email = headerParts[2];
            var name = headerParts[3];

            var author = Rename(new Author { Name = name, Email = email }, renames);

            var insertions = 0;
            var deletions = 0;

            var entries = body.Split("\0".ToArray(), StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var entryParts = entry.Split("\t".ToArray(), 3);

                int ins, del;
                if (!int.TryParse(entryParts[0], out ins) || !int.TryParse(entryParts[1], out del))
                {
                    continue;
                }

                var path = entryParts[2];

                bool keepPath = true;

                for (int i = ignores.Count - 1; i >= 0; i--)
                {
                    var ignore = ignores[i];
                    if (hash.StartsWith(ignore.CommitHash) && ignore.IsMatch(path))
                    {
                        keepPath = ignore.Negated;
                        break;
                    }
                }

                if (keepPath)
                {
                    insertions += ins;
                    deletions += del;
                }
            }

            var commitDay = DateTimeOffset.Parse(date).ToUniversalTime().Date;
            var dayOffset = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek - commitDay.DayOfWeek;
            var commitWeek = commitDay.AddDays(dayOffset + (dayOffset > 0 ? -7 : 0));

            return new UserImpact
            {
                Author = author.Name,
                Commits = 1,
                Insertions = insertions,
                Deletions = deletions,
                Impact = Math.Max(insertions, deletions),
                Week = commitWeek,
            };
        }

        public static Regex GlobToRegex(string glob)
        {
            var tokenized = Regex.Matches(glob, @"\G(?:(?<literal>[^?*[]+)|(?<wildcard>[*?])|(?<class>\[!?\]?([^][]|\[(:[^]:]*:|\.[^].]*\.|=[^]=]*=)\])*\]))");
            var text = new StringBuilder(glob.Length * 2).Append(@"\A");

            if (tokenized.Count == 0)
            {
                throw new ConvertGlobFailedException("Syntax error at character 0.");
            }

            var last = tokenized.Cast<Match>().Last();
            var index = last.Index + last.Length;
            if (index != glob.Length)
            {
                throw new ConvertGlobFailedException("Syntax error at character " + index + ".");
            }

            foreach (Match token in tokenized)
            {
                var literal = token.Groups["literal"];
                var wildcard = token.Groups["wildcard"];
                var charClass = token.Groups["class"];

                if (literal.Success)
                {
                    text.Append(Regex.Escape(literal.Value));
                }
                else if (wildcard.Success)
                {
                    switch (wildcard.Value)
                    {
                        case "?": text.Append(@"[^/]"); break;
                        case "*": text.Append(@"[^/]*"); break;
                    }
                }
                else if (charClass.Success)
                {
                    var content = charClass.Value;
                    content = content.Substring(1, content.Length - 2);

                    bool negate = false;

                    if (content.StartsWith("!"))
                    {
                        negate = true;
                        content = content.Substring(1);
                    }

                    var chunks = Regex.Matches(content, @"(?<literal>[^][])|\[(?::(?<named>[^]:]*):|\.(?<collating>[^].]*)\.|=(?<equivalence>[^]=]*)=)\]");

                    text.Append("[");
                    text.Append(negate ? "^" : "");

                    foreach (Match chunk in chunks)
                    {
                        literal = chunk.Groups["literal"];
                        var named = chunk.Groups["named"];

                        if (literal.Success)
                        {
                            if (literal.Value.Contains("/"))
                            {
                                throw new ConvertGlobFailedException("Forward-slant characters are not valid in glob character classes.");
                            }

                            text.Append(literal.Value.Replace(@"\", @"\\").Replace(@"]", @"\]"));
                        }
                        else if (named.Success)
                        {
                            switch (named.Value.ToLowerInvariant())
                            {
                                case "alnum": text.Append(@"\p{Lu}\p{Ll}\p{Lt}\p{Nd}"); break;
                                case "alpha": text.Append(@"\p{Lu}\p{Ll}\p{Lt}"); break;
                                case "blank": text.Append(@"\f\n\r\t\v\x85\p{Z}"); break;
                                case "cntrl": text.Append(@"\p{Cc}"); break;
                                case "digit": text.Append(@"\p{Nd}"); break;
                                case "lower": text.Append(@"\p{Ll}"); break;
                                case "space": text.Append(@"\p{Zs}"); break;
                                case "upper": text.Append(@"\p{Lu}"); break;
                                case "xdigit": text.Append(@"\p{Nd}a-fA-F"); break;
                                default: throw new ConvertGlobFailedException("The named character class '" + named.Value + "' is supported.");
                            }
                        }
                        else
                        {
                            throw new ConvertGlobFailedException("Collating and equivalence classes are not supported.");
                        }
                    }

                    text.Append("]");
                }
            }

            return new Regex(text.Append(@"\z").ToString(), RegexOptions.Compiled);
        }

        public static List<DiffInfo> GetDiffInfo(string repoPath, string commit)
        {
            var diffs = new List<DiffInfo>();
            List<string> diffLines = null;

            Action addLastDiff = () =>
            {
                if (diffLines != null)
                {
                    diffs.Add(new DiffInfo(diffLines));
                }
            };

            using (var git = Start(string.Format("diff-tree -p -c -r {0}", Q(commit)), repoPath))
            {
                while (!git.StandardOutput.EndOfStream)
                {
                    var line = git.StandardOutput.ReadLine();

                    if (diffLines == null && !line.StartsWith("diff"))
                    {
                        continue;
                    }

                    if (line.StartsWith("diff"))
                    {
                        addLastDiff();
                        diffLines = new List<string> { line };
                    }
                    else
                    {
                        diffLines.Add(line);
                    }
                }
            }

            addLastDiff();

            return diffs;
        }

        public static TreeView GetTreeInfo(string repoPath, string tree, string path = null)
        {
            if (string.IsNullOrEmpty(tree))
            {
                throw new ArgumentNullException("tree");
            }

            if (!Regex.IsMatch(tree, "^[-.a-zA-Z0-9]+$"))
            {
                throw new ArgumentOutOfRangeException("tree", "tree mush be the id of a tree-ish object.");
            }

            path = path ?? string.Empty;
            var results = Execute(string.Format("ls-tree -l -z {0}:{1}", Q(tree), Q(path)), repoPath, Encoding.UTF8, trustErrorCode: true);

            Func<string, ObjectInfo> parseResults = result =>
            {
                var mode = ParseTreePart(result, "[ ]+", out result);
                var type = ParseTreePart(result, "[ ]+", out result);
                var hash = ParseTreePart(result, "[ ]+", out result);
                var size = ParseTreePart(result, "\\t+", out result);
                var name = result;

                return new ObjectInfo(
                    (ObjectType)Enum.Parse(typeof(ObjectType), type, ignoreCase: true),
                    hash,
                    size == "-" ? (int?)null : int.Parse(size),
                    name);
            };

            var objects = from r in results.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                          select parseResults(r);
            return new TreeView(tree, path, objects);
        }

        public static Process StartGetBlob(string repoPath, string tree, string path)
        {
            if (string.IsNullOrEmpty(tree))
            {
                throw new ArgumentNullException("tree");
            }

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            if (!Regex.IsMatch(tree, "^[-a-zA-Z0-9]+$"))
            {
                throw new ArgumentOutOfRangeException("tree", "tree mush be the id of a tree-ish object.");
            }

            return Start(string.Format("show {0}:{1}", Q(tree), Q(path)), repoPath, redirectInput: false);
        }

        public static MemoryStream GetBlob(string repoPath, string tree, string path)
        {
            MemoryStream blob = null;
            try
            {
                blob = new MemoryStream();
                using (var git = StartGetBlob(repoPath, tree, path))
                {
                    var buffer = new byte[1048576];
                    var readCount = 0;
                    while ((readCount = git.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        blob.Write(buffer, 0, readCount);
                    }
                }

                blob.Seek(0, SeekOrigin.Begin);

                var tempBlob = blob;
                blob = null;
                return tempBlob;
            }
            finally
            {
                if (blob != null)
                {
                    blob.Dispose();
                }
            }
        }

        public static void CreateRepo(string repoPath)
        {
            var workingDir = Path.GetDirectoryName(repoPath);
            var results = Execute(string.Format("init --bare {0}", Q(repoPath)), workingDir, trustErrorCode: true);
        }

        public static void ExecutePostCreateHook(string repoPath)
        {
            var sh = WebConfigurationManager.AppSettings["ShCommand"];

            // If 'sh.exe' is not configured, derive the path relative to the git.exe command path.
            if (string.IsNullOrEmpty(sh))
            {
                var git = WebConfigurationManager.AppSettings["GitCommand"];
                sh = Path.Combine(Path.GetDirectoryName(git), "sh.exe");
            }

            // Find the path of the post-create hook.
            var repositories = WebConfigurationManager.AppSettings["RepositoriesPath"];
            var hookRelativePath = WebConfigurationManager.AppSettings["PostCreateHook"];

            // If the hook path is not configured, default to a path of "post-create", relative to the repository directory.
            if (string.IsNullOrEmpty(hookRelativePath))
            {
                hookRelativePath = "post-create";
            }

            // Get the full path info for the hook file, and ensure that it exists.
            var hookFile = new FileInfo(Path.Combine(repositories, hookRelativePath));
            if (!hookFile.Exists)
            {
                return;
            }

            // Prepare to start sh.exe like: `sh.exe -- "C:\Path\To\Hook-Script"`.
            var startInfo = new ProcessStartInfo(sh, string.Format("-- {0}", Q(hookFile.FullName)))
            {
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.EnvironmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH") + Path.PathSeparator + Path.GetDirectoryName(sh);

            // Start the script and wait for exit.
            using (var script = Process.Start(startInfo))
            {
                script.WaitForExit();
            }
        }

        private static string ParseResultLine(string prefix, string result, out string rest)
        {
            var parts = result.Split(new[] { '\n' }, 2);
            rest = parts[1];
            return parts[0].Substring(prefix.Length);
        }

        private static string ParseTreePart(string result, string delimiterPattern, out string rest)
        {
            var match = Regex.Match(result, delimiterPattern);

            if (!match.Success)
            {
                rest = result;
                return null;
            }
            else
            {
                rest = result.Substring(match.Index + match.Length);
                return result.Substring(0, match.Index);
            }
        }
    }
}
