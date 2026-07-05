using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;

namespace Clever.ViewModel.PanelsVM
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

        internal static ObservableCollection<GitGraphRowVM> BuildRows(IList<GitCommitVM> commits)
        {
            var rows = new ObservableCollection<GitGraphRowVM>();
            var activeLanes = new List<string>();

            foreach (var commit in commits)
            {
                var laneIndex = activeLanes.IndexOf(commit.Hash);
                if (laneIndex < 0)
                {
                    activeLanes.Insert(0, commit.Hash);
                    laneIndex = 0;
                }

                var nextLanes = new List<string>(activeLanes);
                var rowHeight = GetRowHeight(commit);
                var row = new GitGraphRowVM
                {
                    Commit = commit,
                    NodeX = GetLaneX(laneIndex),
                    NodeY = NodeY,
                    CommitHeight = CommitHeight,
                    RowHeight = rowHeight,
                    NodeBrush = GetLaneBrush(laneIndex),
                    GraphWidth = GetGraphWidth(activeLanes.Count)
                };

                AddIncomingLines(row, activeLanes, laneIndex, rows.Count == 0);
                UpdateLanesAndConnectParents(row, nextLanes, laneIndex, commit);

                row.GraphWidth = GetGraphWidth(activeLanes.Count > nextLanes.Count ? activeLanes.Count : nextLanes.Count);
                rows.Add(row);
                activeLanes = NormalizeLanes(nextLanes);
            }

            return rows;
        }

        private static void AddIncomingLines(GitGraphRowVM row, IList<string> activeLanes, int commitLane, bool isFirstRow)
        {
            for (var i = 0; i < activeLanes.Count; i++)
            {
                var x = GetLaneX(i);
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
                nextLanes.RemoveAt(commitLane);
                return;
            }

            var firstParent = commit.ParentHashes[0];
            var existingFirstParentLane = FindLane(nextLanes, firstParent, commitLane);
            if (existingFirstParentLane >= 0)
            {
                row.Segments.Add(CreateSegment(row.NodeX, NodeY + NodeRadius, GetLaneX(existingFirstParentLane), row.RowHeight, GetLaneBrush(existingFirstParentLane), 2));
                nextLanes.RemoveAt(commitLane);
            }
            else
            {
                nextLanes[commitLane] = firstParent;
                row.Segments.Add(CreateSegment(row.NodeX, NodeY + NodeRadius, row.NodeX, row.RowHeight, GetLaneBrush(commitLane), 2));
            }

            for (var i = 1; i < commit.ParentHashes.Count; i++)
            {
                var parent = commit.ParentHashes[i];
                var targetLane = nextLanes.IndexOf(parent);
                if (targetLane < 0)
                {
                    targetLane = commitLane + i;
                    if (targetLane > nextLanes.Count)
                        targetLane = nextLanes.Count;
                    nextLanes.Insert(targetLane, parent);
                }

                row.Segments.Add(CreateSegment(row.NodeX, NodeY + NodeRadius, GetLaneX(targetLane), row.RowHeight, GetLaneBrush(targetLane), 2));
            }
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
                if (!string.IsNullOrEmpty(lane) && !result.Contains(lane))
                    result.Add(lane);
            }
            return result;
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
                StrokeThickness = thickness
            };
        }

        private static double GetLaneX(int lane)
        {
            return 10 + lane * LaneWidth;
        }

        private static double GetGraphWidth(int laneCount)
        {
            return 24 + laneCount * LaneWidth;
        }

        private static Brush GetLaneBrush(int lane)
        {
            return LaneBrushes[lane % LaneBrushes.Length];
        }

        private static Brush CreateBrush(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
