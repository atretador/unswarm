package commands

import (
	"fmt"
	"strings"
)

// lcs computes the longest common subsequence of two string slices.
func lcs(a, b []string) [][]int {
	m, n := len(a), len(b)
	dp := make([][]int, m+1)
	for i := range dp {
		dp[i] = make([]int, n+1)
	}

	for i := 1; i <= m; i++ {
		for j := 1; j <= n; j++ {
			if a[i-1] == b[j-1] {
				dp[i][j] = dp[i-1][j-1] + 1
			} else if dp[i-1][j] > dp[i][j-1] {
				dp[i][j] = dp[i-1][j]
			} else {
				dp[i][j] = dp[i][j-1]
			}
		}
	}
	return dp
}

// diff computes a unified diff between two texts and returns it as a string.
func diff(oldText, newText string) string {
	oldLines := strings.Split(oldText, "\n")
	newLines := strings.Split(newText, "\n")

	dp := lcs(oldLines, newLines)

	// Backtrack to find the diff
	i, j := len(oldLines), len(newLines)
	var result []string

	for i > 0 || j > 0 {
		if i > 0 && j > 0 && oldLines[i-1] == newLines[j-1] {
			result = append(result, " "+oldLines[i-1])
			i--
			j--
		} else if j > 0 && (i == 0 || dp[i][j-1] >= dp[i-1][j]) {
			result = append(result, "+"+newLines[j-1])
			j--
		} else {
			result = append(result, "-"+oldLines[i-1])
			i--
		}
	}

	// Reverse result
	for left, right := 0, len(result)-1; left < right; left, right = left+1, right-1 {
		result[left], result[right] = result[right], result[left]
	}

	return strings.Join(result, "\n")
}

// DiffOutput represents the JSON output for diff command.
type DiffOutput struct {
	FromVersion int    `json:"from_version"`
	ToVersion   int    `json:"to_version"`
	DiffText    string `json:"diff_text"`
}

// FormatDiff formats a diff in unified format with header.
func FormatDiff(fromVersion, toVersion int, oldText, newText string) string {
	d := diff(oldText, newText)
	return fmt.Sprintf("--- version %d\n+++ version %d\n%s", fromVersion, toVersion, d)
}
