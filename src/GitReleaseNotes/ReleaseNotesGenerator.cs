﻿using GitTools;
using GitTools.Git;
using GitTools.IssueTrackers;

namespace GitReleaseNotes
{
    using System;
    using System.IO;
    using System.Linq;
    using GitReleaseNotes.FileSystem;
    using GitReleaseNotes.Git;
    using GitReleaseNotes.IssueTrackers;
    using LibGit2Sharp;

    public class ReleaseNotesGenerator
    {
        private static readonly ILog Log = GitReleaseNotesEnvironment.Log;

        private readonly Context _context;
        private readonly IFileSystem _fileSystem;
        private readonly IIssueTrackerFactory _issueTrackerFactory;

        public ReleaseNotesGenerator(Context context, IFileSystem fileSystem, IIssueTrackerFactory issueTrackerFactory)
        {
            _context = context;
            _fileSystem = fileSystem;
            _issueTrackerFactory = issueTrackerFactory;
        }

        public SemanticReleaseNotes GenerateReleaseNotes()
        {
            var context = _context;

            using (var temporaryFilesContext = new TemporaryFilesContext())
            {
                var gitPreparer = new GitPreparer();
                var gitRepositoryDirectory = gitPreparer.Prepare(context.Repository, temporaryFilesContext);
                var gitRepository = new Repository(gitRepositoryDirectory);

                if (context.IssueTracker == null)
                {
                    // TODO: Write auto detection mechanism which is better than this
                    throw new GitReleaseNotesException("Feature to automatically detect issue tracker must be written");
                    //var firstOrDefault = _issueTrackers.FirstOrDefault(i => i.Value.RemotePresentWhichMatches);
                    //if (firstOrDefault.Value != null)
                    //{
                    //    issueTracker = firstOrDefault.Value;
                    //}
                }

                var issueTracker = _issueTrackerFactory.CreateIssueTracker(context.IssueTracker);
                if (issueTracker == null)
                {
                    throw new GitReleaseNotesException("Failed to create issue tracker from context, cannot continue");
                }

                var releaseFileWriter = new ReleaseFileWriter(_fileSystem);
                string outputFile = null;
                var previousReleaseNotes = new SemanticReleaseNotes();

                var outputPath = gitRepositoryDirectory;
                var outputDirectory = new DirectoryInfo(outputPath);
                if (outputDirectory.Name == ".git")
                {
                    outputPath = outputDirectory.Parent.FullName;
                }

                if (!string.IsNullOrEmpty(context.OutputFile))
                {
                    outputFile = Path.IsPathRooted(context.OutputFile)
                        ? context.OutputFile
                        : Path.Combine(outputPath, context.OutputFile);
                    previousReleaseNotes = new ReleaseNotesFileReader(_fileSystem, outputPath).ReadPreviousReleaseNotes(outputFile);
                }

                var categories = new Categories(context.Categories, context.AllLabels);
                var tagToStartFrom = context.AllTags
                    ? gitRepository.GetFirstCommit()
                    : gitRepository.GetLastTaggedCommit() ?? gitRepository.GetFirstCommit();
                var currentReleaseInfo = gitRepository.GetCurrentReleaseInfo();
                if (!string.IsNullOrEmpty(context.Version))
                {
                    currentReleaseInfo.Name = context.Version;
                    currentReleaseInfo.When = DateTimeOffset.Now;
                }

                var releaseNotes = GenerateReleaseNotes(
                    context, gitRepository, issueTracker,
                    previousReleaseNotes, categories,
                    tagToStartFrom, currentReleaseInfo);

                var releaseNotesOutput = releaseNotes.ToString();
                releaseFileWriter.OutputReleaseNotesFile(releaseNotesOutput, outputFile);

                return releaseNotes;
            }
        }

        public static SemanticReleaseNotes GenerateReleaseNotes(Context context,
            IRepository gitRepo, IIssueTracker issueTracker, SemanticReleaseNotes previousReleaseNotes,
            Categories categories, TaggedCommit tagToStartFrom, ReleaseInfo currentReleaseInfo)
        {
            var releases = ReleaseFinder.FindReleases(gitRepo, tagToStartFrom, currentReleaseInfo);
            var findIssuesSince =
                IssueStartDateBasedOnPreviousReleaseNotes(gitRepo, previousReleaseNotes)
                ??
                tagToStartFrom.Commit.Author.When;

            var closedIssues = issueTracker.GetIssues("status in (closed, done)");
            //var closedIssues = issueTracker.GetClosedIssues(context.IssueTracker, findIssuesSince).ToArray();

            var semanticReleases = (
                from release in releases
                let releaseNoteItems = closedIssues
                    .Where(i => (release.When == null || i.DateClosed < release.When) && (release.PreviousReleaseDate == null || i.DateClosed > release.PreviousReleaseDate))
                    //.Select(i => new ReleaseNoteItem(i.Title, i.Id, i.Url, i.Labels, i.DateClosed, i.Contributors))
                    .Select(i => new ReleaseNoteItem(i.Title, i.Id, i.Url, i.Labels, i.DateClosed, new Contributor[] { /*TODO: implement*/ }))
                    .ToList<IReleaseNoteLine>()
                let beginningSha = release.FirstCommit == null ? null : release.FirstCommit.Substring(0, 10)
                let endSha = release.LastCommit == null ? null : release.LastCommit.Substring(0, 10)
                select new SemanticRelease(release.Name, release.When, releaseNoteItems, new ReleaseDiffInfo
                {
                    BeginningSha = beginningSha,
                    EndSha = endSha,
                    //DiffUrlFormat = issueTracker.DiffUrlFormat
                })).ToList();

            return new SemanticReleaseNotes(semanticReleases, categories).Merge(previousReleaseNotes);
        }

        private static DateTimeOffset? IssueStartDateBasedOnPreviousReleaseNotes(IRepository gitRepo,
            SemanticReleaseNotes previousReleaseNotes)
        {
            var lastGeneratedRelease = previousReleaseNotes.Releases.FirstOrDefault();
            if (lastGeneratedRelease == null) return null;
            var endSha = lastGeneratedRelease.DiffInfo.EndSha;
            if (string.IsNullOrEmpty(endSha))
            {
                lastGeneratedRelease = previousReleaseNotes.Releases.Skip(1).FirstOrDefault();
                if (lastGeneratedRelease != null)
                {
                    endSha = lastGeneratedRelease.DiffInfo.EndSha;
                }
            }

            if (string.IsNullOrEmpty(endSha)) return null;
            var commitToStartFrom = gitRepo.Commits.FirstOrDefault(c => c.Sha.StartsWith(endSha));
            if (commitToStartFrom != null)
            {
                return commitToStartFrom.Author.When;
            }
            return null;
        }
    }
}