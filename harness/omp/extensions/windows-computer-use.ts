// OMP extension for the Windows computer-use MCP server.
// Install to ~/.omp/agent/extensions/windows-computer-use.ts (scripts/install-omp.ps1 does this).
//
// The server announces its kill switch (Ctrl+Alt+Pause by default) as the MCP notification
// `notifications/windows/kill_switch` with params { reason, revokedGrants, at, message }.
// OMP's default `tools.approvalMode` is `yolo`, so nothing else stops a run: this extension
// aborts the current agent turn, warns the user, and leaves a next-turn note so the model
// does not resume desktop actions on its own.
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";

interface KillSwitchParams {
	reason?: string;
	revokedGrants?: number;
	message?: string;
}

const nextTurnNote = (reason: string) =>
	`The Windows computer-use kill switch fired (reason: ${reason}). ` +
	"All windows_* control grants were revoked and the current run was aborted. " +
	"Do NOT resume desktop actions with the windows_* tools without new instructions from the user.";

export default function (pi: ExtensionAPI) {
	pi.on("mcp_notification", async (event, ctx) => {
		if (event.server !== "windows" || event.method !== "notifications/windows/kill_switch") {
			return;
		}
		const params = (event.params ?? {}) as KillSwitchParams;
		const reason = params.reason ?? "unspecified";
		ctx.abort();
		ctx.ui.notify(`Windows kill switch engaged: ${reason}; control grants revoked`, "warning");
		pi.sendUserMessage(nextTurnNote(reason), { deliverAs: "nextTurn" });
	});

	// Manual fallback if the hotkey cannot be pressed (headless, remote session, hotkey taken).
	pi.registerCommand("windows-killswitch", {
		description: "Abort the current agent run as if the Windows computer-use kill switch fired",
		handler: async (_args, ctx) => {
			ctx.abort();
			ctx.ui.notify("Windows kill switch engaged (manual); control grants revoked", "warning");
			pi.sendUserMessage(nextTurnNote("manual /windows-killswitch command"), {
				deliverAs: "nextTurn",
			});
		},
	});
}
