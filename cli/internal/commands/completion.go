package commands

import (
	"os"

	"github.com/spf13/cobra"
)

var completionCmd = &cobra.Command{
	Use:   "completion [bash|zsh|fish|powershell]",
	Short: "Generate shell completion scripts",
	Long: `Generate shell completion scripts for the unswarm CLI.

To load completions:

Bash:

  $ source <(unswarm completion bash)

  # To load completions for each session, execute once:
  # Linux:
  $ unswarm completion bash > /etc/bash_completion.d/unswarm
  # macOS:
  $ unswarm completion bash > $(brew --prefix)/etc/bash_completion.d/unswarm

Zsh:

  # If shell completion is not already enabled in your environment,
  # you will need to enable it. You can execute the following once:
  $ echo "autoload -U compinit; compinit" >> ~/.zshrc

  # To load completions for each session, execute once:
  $ unswarm completion zsh > "${fpath[1]}/_unswarm"

Fish:

  $ unswarm completion fish | source

  # To load completions for each session, execute once:
  $ unswarm completion fish > ~/.config/fish/completions/unswarm.fish

PowerShell:

  PS> unswarm completion powershell | Out-String | Invoke-Expression

  # To load completions for every new session, add output to your profile.ps1.
`,
	DisableFlagsInUseLine: true,
	ValidArgs:             []string{"bash", "zsh", "fish", "powershell"},
	Args:                  cobra.MatchAll(cobra.ExactArgs(1), cobra.OnlyValidArgs),
	RunE: func(cmd *cobra.Command, args []string) error {
		switch args[0] {
		case "bash":
			return cmd.Root().GenBashCompletionV2(os.Stdout, true)
		case "zsh":
			return cmd.Root().GenZshCompletion(os.Stdout)
		case "fish":
			return cmd.Root().GenFishCompletion(os.Stdout, true)
		case "powershell":
			return cmd.Root().GenPowerShellCompletionWithDesc(os.Stdout)
		}
		return nil // unreachable due to ValidArgs
	},
}

func init() {
	rootCmd.AddCommand(completionCmd)
}
