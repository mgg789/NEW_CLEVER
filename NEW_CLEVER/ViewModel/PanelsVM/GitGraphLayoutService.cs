using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace NEW_CLEVER.ViewModel.PanelsVM
{
    internal static class GitGraphLayoutService
    {
        internal const double LaneWidth = 18;
        internal const double CommitHeight = 34;
        private const double FileRowHeight = 26;
        private const double ExpandedFilesPadding = 4;
        private const double NodeY = 17;
        private const double NodeRadius = 5;

        private static readonly Brush[] LaneBrushes =
        {
            CreateBrush("#FFB700"),
            CreateBrush("#E73F96"),
            CreateBrush("#46A6D9"),
            CreateBrush("#41B883"),
            CreateBrush("#9E6BE8"),
            CreateBrush("#D47A00")
        };

        internal static ObservableCollection<GitGraphRowVM> BuildRows(IList<GitCommitVM> commits, string currentBranch)
        {
            var rows = new ObservableCollection<GitGraphRowVM>();
            var activeLanes = new List<string>();
            var branchLanes = BuildBranchLanes(commits, currentBranch);
            var activeBranchLane = GetBranchLane(branchLanes, NormalizeBranchName(currentBranch), -1);

            foreach (var commit in commits)
                commit.Markers.Clear();

            foreach (var commit in commits)
            {
                var previousLanes = new List<string>(activeLanes);
                var preferredLane = GetPreferredLane(commit, branchLanes);
                var incomingLane = activeLanes.IndexOf(commit.Hash);
                var redirectedLane = -1;
                int laneIndex;

                if (incomingLane >= 0 && preferredLane >= 0 && preferredLane != incomingLane && IsLaneAvailableFor(activeLanes, preferredLane, commit.Hash))
                {
                    laneIndex = preferredLane;
                    EnsureLane(activeLanes, laneIndex);
                    activeLanes[laneIndex] = commit.Hash;
                    redirectedLane = incomingLane;
                }
                else if (incomingLane >= 0)
                {
                    laneIndex = incomingLane;
                }
                else
                {
                    laneIndex = AssignLane(activeLanes, preferredLane, commit.Hash);
                }

                var nextLanes = new List<string>(activeLanes);
                if (redirectedLane >= 0)
                    ClearLane(nextLanes, redirectedLane);

                var rowHeight = GetRowHeight(commit);
                var row = new GitGraphRowVM
                {
                    Commit = commit,
                    NodeX = GetLaneX(laneIndex),
                    NodeY = NodeY,
                    CommitHeight = CommitHeight,
                    RowHeight = rowHeight,
                    NodeBrush = GetLaneBrush(laneIndex),
                    GraphWidth = GetGraphWidth(Math.Max(activeLanes.Count, branchLanes.Count))
                };

                BuildCommitMarkers(commit, branchLanes, laneIndex, activeBranchLane);
                AddIncomingLines(row, previousLanes, laneIndex, rows.Count == 0, redirectedLane);
                UpdateLanesAndConnectParents(row, nextLanes, laneIndex, commit);

                row.GraphWidth = GetGraphWidth(Math.Max(Math.Max(activeLanes.Count, nextLanes.Count), branchLanes.Count));
                rows.Add(row);
                activeLanes = NormalizeLanes(nextLanes);
            }

            return rows;
        }

        private static Dictionary<string, int> BuildBranchLanes(IList<GitCommitVM> commits, string currentBranch)
        {
            var branchNames = new List<string>();
            var current = NormalizeBranchName(currentBranch);
            AddBranchName(branchNames, current);

            foreach (var commit in commits)
            {
                foreach (var branch in GetLocalBranchNames(commit).Concat(GetRemoteBranchNames(commit)))
                    AddBranchName(branchNames, branch);
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var mainBranch = branchNames.FirstOrDefault(n => string.Equals(n, "main", StringComparison.OrdinalIgnoreCase))
                ?? branchNames.FirstOrDefault(n => string.Equals(n, "master", StringComparison.OrdinalIgnoreCase))
                ?? branchNames.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(mainBranch))
                map[mainBranch] = 0;

            if (!string.IsNullOrWhiteSpace(current) && !map.ContainsKey(current))
            {
                map[current] = map.Count == 0 ? 0 : 1;
            }

            return map;
        }

        private static void BuildCommitMarkers(GitCommitVM commit, IDictionary<string, int> branchLanes, int fallbackLane, int activeBranchLane)
        {
            if (commit.IsHead)
            {
                commit.Markers.Add(new GitCommitMarkerVM
                {
                    Kind = GitCommitMarkerKind.Head,
                    Text = "Current files",
                    Brush = GetLaneBrush(activeBranchLane >= 0 ? activeBranchLane : fallbackLane)
                });
            }

            var displayedRemoteBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var remote in commit.RemoteRefs)
            {
                var branch = NormalizeBranchName(remote);
                if (string.IsNullOrWhiteSpace(branch) || !displayedRemoteBranches.Add(branch))
                    continue;

                var lane = GetBranchLane(branchLanes, branch, fallbackLane);
                commit.Markers.Add(new GitCommitMarkerVM
                {
                    Kind = GitCommitMarkerKind.Remote,
                    Text = remote,
                    Brush = GetLaneBrush(lane)
                });
            }

            foreach (var branch in GetLocalBranchNames(commit).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var lane = GetBranchLane(branchLanes, branch, fallbackLane);
                commit.Markers.Add(new GitCommitMarkerVM
                {
                    Kind = GitCommitMarkerKind.Branch,
                    Text = branch,
                    Brush = GetLaneBrush(lane)
                });
            }
        }

        private static void AddIncomingLines(GitGraphRowVM row, IList<string> activeLanes, int commitLane, bool isFirstRow, int redirectedLane)
        {
            for (var i = 0; i < activeLanes.Count; i++)
            {
                if (string.IsNullOrEmpty(activeLanes[i]))
                    continue;

                var x = GetLaneX(i);
                if (i == redirectedLane)
                {
                    row.Segments.Add(CreateSegment(x, 0, x, NodeY, GetLaneBrush(i), 2));
                    AddSideConnector(row, i, GetLaneBrush(i), false);
                    continue;
                }

                if (i == commitLane)
                {
                    if (isFirstRow)
                        continue;

                    row.Segments.Add(CreateSegment(x, 0, x, NodeY - NodeRadius, GetLaneBrush(i), 2));
                }
                else
                {
                    row.Segments.Add(CreateSegment(x, 0, x, row.RowHeight, GetLaneBrush(i), 2));
                }
            }
        }

        private static void UpdateLanesAndConnectParents(GitGraphRowVM row, List<string> nextLanes, int commitLane, GitCommitVM commit)
        {
            if (commit.ParentHashes.Count == 0)
            {
                ClearLane(nextLanes, commitLane);
                return;
            }

            var firstParent = commit.ParentHashes[0];
            var existingFirstParentLane = FindLane(nextLanes, firstParent, commitLane);
            if (existingFirstParentLane >= 0)
            {
                AddSideConnector(row, existingFirstParentLane, GetLaneBrush(existingFirstParentLane), false);
                ClearLane(nextLanes, commitLane);
            }
            else
            {
                EnsureLane(nextLanes, commitLane);
                nextLanes[commitLane] = firstParent;
                row.Segments.Add(CreateSegment(row.NodeX, NodeY + NodeRadius, row.NodeX, row.RowHeight, GetLaneBrush(commitLane), 2));
            }

            for (var i = 1; i < commit.ParentHashes.Count; i++)
            {
                var parent = commit.ParentHashes[i];
                var targetLane = nextLanes.IndexOf(parent);
                var insertedLane = false;
                if (targetLane < 0)
                {
                    targetLane = FindReusableLane(nextLanes, commitLane + 1);
                    if (targetLane >= 0)
                    {
                        nextLanes[targetLane] = parent;
                    }
                    else
                    {
                        targetLane = nextLanes.Count;
                        nextLanes.Add(parent);
                    }

                    insertedLane = true;
                }

                AddSideConnector(row, targetLane, GetLaneBrush(targetLane), insertedLane);
            }
        }

        private static void AddSideConnector(GitGraphRowVM row, int targetLane, Brush stroke, bool drawTargetContinuation)
        {
            var targetX = GetLaneX(targetLane);
            var nodeSideX = targetX < row.NodeX ? row.NodeX - NodeRadius : row.NodeX + NodeRadius;
            if (drawTargetContinuation)
                row.Segments.Add(CreateSegment(targetX, NodeY, targetX, row.RowHeight, stroke, 2));

            row.Segments.Add(CreateSegment(targetX, NodeY, nodeSideX, NodeY, stroke, 2));
        }

        private static int AssignLane(List<string> activeLanes, int preferredLane, string hash)
        {
            if (preferredLane >= 0)
            {
                EnsureLane(activeLanes, preferredLane);
                if (string.IsNullOrEmpty(activeLanes[preferredLane]))
                {
                    activeLanes[preferredLane] = hash;
                    return preferredLane;
                }

                if (activeLanes[preferredLane] == hash)
                    return preferredLane;

                var reusable = FindReusableLane(activeLanes, preferredLane + 1);
                if (reusable >= 0)
                {
                    activeLanes[reusable] = hash;
                    return reusable;
                }
            }

            activeLanes.Add(hash);
            return activeLanes.Count - 1;
        }

        private static int GetPreferredLane(GitCommitVM commit, IDictionary<string, int> branchLanes)
        {
            var branches = GetLocalBranchNames(commit)
                .Concat(GetRemoteBranchNames(commit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lanes = branches
                .Select(branch => GetBranchLane(branchLanes, branch, -1))
                .Where(lane => lane >= 0)
                .ToList();

            if (lanes.Count == 0 && branches.Count > 0)
                return branchLanes.Count > 0 ? 1 : 0;

            return lanes.Count == 0 ? -1 : lanes.Min();
        }

        private static int GetBranchLane(IDictionary<string, int> branchLanes, string branch, int fallbackLane)
        {
            int lane;
            return !string.IsNullOrWhiteSpace(branch) && branchLanes.TryGetValue(branch, out lane)
                ? lane
                : fallbackLane;
        }

        private static bool IsLaneAvailableFor(IList<string> activeLanes, int lane, string hash)
        {
            return lane >= activeLanes.Count
                || string.IsNullOrEmpty(activeLanes[lane])
                || activeLanes[lane] == hash;
        }

        private static IEnumerable<string> GetLocalBranchNames(GitCommitVM commit)
        {
            if (commit == null)
                yield break;

            foreach (var branch in commit.LocalRefs)
            {
                var normalized = NormalizeBranchName(branch);
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }

        private static IEnumerable<string> GetRemoteBranchNames(GitCommitVM commit)
        {
            if (commit == null)
                yield break;

            foreach (var remote in commit.RemoteRefs)
            {
                var normalized = NormalizeBranchName(remote);
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }

        private static string NormalizeBranchName(string refName)
        {
            if (string.IsNullOrWhiteSpace(refName))
                return null;

            var name = refName.Trim();
            if (name.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "HEAD", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("tag: ", StringComparison.OrdinalIgnoreCase))
                return null;

            var slash = name.IndexOf('/');
            if (slash > 0 && IsKnownRemoteName(name.Substring(0, slash)))
                name = name.Substring(slash + 1);

            if (string.Equals(name, "HEAD", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(" -> ", StringComparison.Ordinal) >= 0)
                return null;

            return name.Length == 0 ? null : name;
        }

        private static bool IsKnownRemoteName(string name)
        {
            return string.Equals(name, "origin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "upstream", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddBranchName(ICollection<string> branchNames, string branch)
        {
            if (string.IsNullOrWhiteSpace(branch))
                return;

            if (!branchNames.Any(item => string.Equals(item, branch, StringComparison.OrdinalIgnoreCase)))
                branchNames.Add(branch);
        }

        private static double GetRowHeight(GitCommitVM commit)
        {
            if (commit == null || !commit.IsExpanded || commit.Files.Count == 0)
                return CommitHeight;

            return CommitHeight + ExpandedFilesPadding + commit.Files.Count * FileRowHeight;
        }

        private static List<string> NormalizeLanes(IEnumerable<string> lanes)
        {
            var result = new List<string>();
            foreach (var lane in lanes)
            {
                if (string.IsNullOrEmpty(lane))
                {
                    result.Add(null);
                }
                else if (!result.Contains(lane))
                {
                    result.Add(lane);
                }
            }

            while (result.Count > 0 && string.IsNullOrEmpty(result[result.Count - 1]))
                result.RemoveAt(result.Count - 1);

            return result;
        }

        private static void ClearLane(IList<string> lanes, int lane)
        {
            if (lane < 0 || lane >= lanes.Count)
                return;

            if (lane == lanes.Count - 1)
                lanes.RemoveAt(lane);
            else
                lanes[lane] = null;
        }

        private static void EnsureLane(IList<string> lanes, int lane)
        {
            while (lanes.Count <= lane)
                lanes.Add(null);
        }

        private static int FindReusableLane(IList<string> lanes, int startLane)
        {
            for (var i = startLane; i < lanes.Count; i++)
            {
                if (string.IsNullOrEmpty(lanes[i]))
                    return i;
            }

            return -1;
        }

        private static int FindLane(IList<string> lanes, string hash, int ignoredLane)
        {
            for (var i = 0; i < lanes.Count; i++)
            {
                if (i != ignoredLane && lanes[i] == hash)
                    return i;
            }

            return -1;
        }

        private static GitGraphSegmentVM CreateSegment(double x1, double y1, double x2, double y2, Brush stroke, double thickness)
        {
            return new GitGraphSegmentVM
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = stroke,
                StrokeThickness = thickness,
                Points = new PointCollection
                {
                    new Point(x1, y1),
                    new Point(x2, y2)
                }
            };
        }

        private static double GetLaneX(int lane)
        {
            return 10 + lane * LaneWidth;
        }

        private static double GetGraphWidth(int laneCount)
        {
            return 24 + Math.Max(1, laneCount) * LaneWidth;
        }

        private static Brush GetLaneBrush(int lane)
        {
            return LaneBrushes[Math.Max(0, lane) % LaneBrushes.Length];
        }

        private static Brush CreateBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
