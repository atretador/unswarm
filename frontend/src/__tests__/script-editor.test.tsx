import { describe, it, expect, beforeEach, vi } from "vitest";
import {
  render,
  screen,
  waitFor,
  fireEvent,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ScriptInfo } from "../lib/api/types";

// The global setup mock only exposes httpClient + BASE_URL; ScriptEditor imports
// ApiError, so expose a compatible class while keeping the client aliased to mockClient.
vi.mock("../lib/api/httpClient", async () => {
  const { mockClient } = await import("../lib/api/mock");
  class ApiError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.name = "ApiError";
      this.status = status;
    }
  }
  return { ApiError, httpClient: mockClient, BASE_URL: "http://localhost:5014" };
});

import { mockClient, setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import {
  ScriptDropZone,
  ScriptCardGrid,
  ScriptEditorDialog,
  AgentScriptUpload,
} from "../features/swarm/ScriptEditor";

function shFile(name: string, size = 16): File {
  return new File([new Uint8Array(size)], name, { type: "text/x-shellscript" });
}

function script(name: string, overrides: Partial<ScriptInfo> = {}): ScriptInfo {
  return {
    name,
    path: `/home/user/scripts/${name}`,
    sizeBytes: 512,
    lastModified: new Date().toISOString(),
    ...overrides,
  };
}

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

// ─── ScriptDropZone ───────────────────────────────────────────────

describe("ScriptDropZone", () => {
  const fileInput = (c: HTMLElement) =>
    c.querySelector('input[type="file"]') as HTMLInputElement;

  it("renders the browse prompt", () => {
    render(
      <TestWrapper>
        <ScriptDropZone onFilesSelected={() => {}} />
      </TestWrapper>,
    );
    expect(screen.getByText("Drop .sh files here or click to browse")).toBeInTheDocument();
  });

  it("accepts a valid .sh file", () => {
    const onFilesSelected = vi.fn();
    const { container } = render(
      <TestWrapper>
        <ScriptDropZone onFilesSelected={onFilesSelected} />
      </TestWrapper>,
    );

    const file = shFile("run.sh");
    fireEvent.change(fileInput(container), { target: { files: [file] } });

    expect(onFilesSelected).toHaveBeenCalledWith([file]);
  });

  it("rejects non-.sh files with an error", () => {
    const onFilesSelected = vi.fn();
    const { container } = render(
      <TestWrapper>
        <ScriptDropZone onFilesSelected={onFilesSelected} />
      </TestWrapper>,
    );

    fireEvent.change(fileInput(container), {
      target: { files: [new File(["x"], "notes.txt")] },
    });

    expect(onFilesSelected).not.toHaveBeenCalled();
    expect(screen.getByText(/notes\.txt: only \.sh files are allowed/)).toBeInTheDocument();
  });

  it("rejects files over the 1 MB limit", () => {
    const onFilesSelected = vi.fn();
    const { container } = render(
      <TestWrapper>
        <ScriptDropZone onFilesSelected={onFilesSelected} />
      </TestWrapper>,
    );

    fireEvent.change(fileInput(container), {
      target: { files: [shFile("big.sh", 1_048_577)] },
    });

    expect(onFilesSelected).not.toHaveBeenCalled();
    expect(screen.getByText(/big\.sh: exceeds 1 MB limit/)).toBeInTheDocument();
  });

  it("handles drag-over styling and a file drop", () => {
    const onFilesSelected = vi.fn();
    const { container } = render(
      <TestWrapper>
        <ScriptDropZone onFilesSelected={onFilesSelected} />
      </TestWrapper>,
    );

    const zone = container.querySelector("button") as HTMLButtonElement;
    fireEvent.dragOver(zone);
    expect(screen.getByText("Drop to upload")).toBeInTheDocument();

    const file = shFile("drop.sh");
    fireEvent.drop(zone, { dataTransfer: { files: [file] } });
    expect(onFilesSelected).toHaveBeenCalledWith([file]);
  });

  it("ignores drops and file picks while disabled", () => {
    const onFilesSelected = vi.fn();
    const { container } = render(
      <TestWrapper>
        <ScriptDropZone onFilesSelected={onFilesSelected} disabled />
      </TestWrapper>,
    );

    const zone = container.querySelector("button") as HTMLButtonElement;
    expect(zone).toBeDisabled();
    const file = shFile("drop.sh");
    fireEvent.drop(zone, { dataTransfer: { files: [file] } });
    expect(onFilesSelected).not.toHaveBeenCalled();
  });
});

// ─── ScriptCardGrid ───────────────────────────────────────────────

describe("ScriptCardGrid", () => {
  const baseProps = {
    selectedName: null as string | null,
    registeredNames: new Set<string>(),
    agentName: "host",
    onSelect: () => {},
    onEdit: () => {},
    onDelete: () => {},
    onRetry: () => {},
  };

  it("renders skeletons while loading", () => {
    const { container } = render(
      <TestWrapper>
        <ScriptCardGrid {...baseProps} scripts={undefined} isLoading error={null} />
      </TestWrapper>,
    );
    expect(container.querySelectorAll('[aria-hidden="true"]').length).toBeGreaterThan(0);
  });

  it("renders an error state with retry", async () => {
    const user = userEvent.setup();
    const onRetry = vi.fn();
    render(
      <TestWrapper>
        <ScriptCardGrid
          {...baseProps}
          scripts={undefined}
          isLoading={false}
          error={new Error("nope")}
          onRetry={onRetry}
        />
      </TestWrapper>,
    );

    expect(screen.getByText("Couldn't list scripts")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Retry" }));
    expect(onRetry).toHaveBeenCalled();
  });

  it("renders the empty state", () => {
    render(
      <TestWrapper>
        <ScriptCardGrid {...baseProps} scripts={[]} isLoading={false} error={null} />
      </TestWrapper>,
    );
    expect(screen.getByText("No scripts uploaded")).toBeInTheDocument();
  });

  it("renders script cards and wires selection/edit/delete", async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    const onEdit = vi.fn();
    const onDelete = vi.fn();

    render(
      <TestWrapper>
        <ScriptCardGrid
          {...baseProps}
          scripts={[script("run.sh")]}
          isLoading={false}
          error={null}
          onSelect={onSelect}
          onEdit={onEdit}
          onDelete={onDelete}
        />
      </TestWrapper>,
    );

    expect(screen.getByText("run.sh")).toBeInTheDocument();
    expect(screen.getByText("512 B")).toBeInTheDocument();
    expect(screen.getByText("just now")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Select" }));
    expect(onSelect).toHaveBeenCalledWith("run.sh");

    await user.click(screen.getByTitle("Edit script content"));
    expect(onEdit).toHaveBeenCalledWith("run.sh");

    await user.click(screen.getByTitle("Delete script"));
    expect(onDelete).toHaveBeenCalledWith("run.sh");
  });

  it("shows the registered badge for already-registered scripts", () => {
    render(
      <TestWrapper>
        <ScriptCardGrid
          {...baseProps}
          scripts={[script("run.sh")]}
          isLoading={false}
          error={null}
          registeredNames={new Set(["run.sh"])}
        />
      </TestWrapper>,
    );
    expect(screen.getByText("registered")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Select" })).not.toBeInTheDocument();
  });
});

// ─── ScriptEditorDialog ───────────────────────────────────────────

describe("ScriptEditorDialog", () => {
  it("loads script content and saves edits", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getScriptContent").mockResolvedValue("#!/bin/bash\necho hi");
    const updateSpy = vi
      .spyOn(mockClient, "updateHostScript")
      .mockResolvedValue(script("sample.sh"));
    const onSaved = vi.fn();
    const onClose = vi.fn();

    render(
      <TestWrapper>
        <ScriptEditorDialog
          scriptName="sample.sh"
          isHost
          agentName="host"
          isRunning={false}
          open
          onClose={onClose}
          onSaved={onSaved}
        />
      </TestWrapper>,
    );

    const textarea = await screen.findByRole("textbox");
    await waitFor(() =>
      expect(textarea).toHaveValue("#!/bin/bash\necho hi"),
    );

    const save = screen.getByRole("button", { name: "Save" });
    expect(save).toBeDisabled();

    await user.type(textarea, "\n# edited");
    await waitFor(() => expect(save).toBeEnabled());
    await user.click(save);

    await waitFor(() => expect(updateSpy).toHaveBeenCalledTimes(1));
    expect(updateSpy.mock.calls[0][0]).toBe("sample.sh");
    expect((updateSpy.mock.calls[0][1] as File).name).toBe("sample.sh");
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(onClose).toHaveBeenCalled();
  });

  it("shows a running warning", async () => {
    vi.spyOn(mockClient, "getScriptContent").mockResolvedValue("# c");
    render(
      <TestWrapper>
        <ScriptEditorDialog
          scriptName="run.sh"
          isHost
          agentName="host"
          isRunning
          open
          onClose={() => {}}
          onSaved={() => {}}
        />
      </TestWrapper>,
    );

    expect(
      await screen.findByText(
        "This script is currently running. Changes will take effect on next start.",
      ),
    ).toBeInTheDocument();
  });

  it("asks to discard when closing with unsaved changes", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getScriptContent").mockResolvedValue("# c");
    const onClose = vi.fn();

    render(
      <TestWrapper>
        <ScriptEditorDialog
          scriptName="run.sh"
          isHost
          agentName="host"
          isRunning={false}
          open
          onClose={onClose}
          onSaved={() => {}}
        />
      </TestWrapper>,
    );

    const textarea = await screen.findByRole("textbox");
    await waitFor(() => expect(textarea).toHaveValue("# c"));
    await user.type(textarea, "\n# dirty");

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.getByText("Discard changes?")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Discard" }));
    expect(onClose).toHaveBeenCalled();
  });

  it("surfaces a save failure", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getScriptContent").mockResolvedValue("# c");
    vi.spyOn(mockClient, "updateHostScript").mockRejectedValue(
      new Error("disk full"),
    );

    render(
      <TestWrapper>
        <ScriptEditorDialog
          scriptName="run.sh"
          isHost
          agentName="host"
          isRunning={false}
          open
          onClose={() => {}}
          onSaved={() => {}}
        />
      </TestWrapper>,
    );

    const textarea = await screen.findByRole("textbox");
    await waitFor(() => expect(textarea).toHaveValue("# c"));
    await user.type(textarea, "x");
    await user.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("disk full")).toBeInTheDocument();
  });
});

// ─── AgentScriptUpload ────────────────────────────────────────────

describe("AgentScriptUpload", () => {
  it("lists scripts, selects one and registers it", async () => {
    const user = userEvent.setup();
    const registerSpy = vi
      .spyOn(mockClient, "registerRuntime")
      .mockResolvedValue({} as never);

    render(
      <TestWrapper>
        <AgentScriptUpload
          agentName="edge-node-1"
          registered={[]}
          onClose={() => {}}
        />
      </TestWrapper>,
    );

    const card = await screen.findByRole("button", { name: /run_llama\.sh/ });
    await user.click(card);

    expect(screen.getByText("Register script")).toBeInTheDocument();
    const displayName = screen.getByLabelText("Display name") as HTMLInputElement;
    expect(displayName.value).toBe("run-llama");

    await user.click(screen.getByRole("button", { name: "Register on edge-node-1" }));

    await waitFor(() => expect(registerSpy).toHaveBeenCalledTimes(1));
    expect(registerSpy.mock.calls[0][0]).toMatchObject({
      displayName: "run-llama",
      launcherPath: "/home/user/scripts/run_llama.sh",
      containerPort: 8080,
      agent: "edge-node-1",
      runtimeKind: "script",
    });
  });

  it("does not nest interactive controls inside the card button", async () => {
    const { container } = render(
      <TestWrapper>
        <AgentScriptUpload agentName="edge-node-1" registered={[]} onClose={() => {}} />
      </TestWrapper>,
    );

    const card = await screen.findByRole("button", { name: /run_llama\.sh/ });

    // Invalid HTML: a <button> must never contain another <button>.
    expect(container.querySelectorAll("button button")).toHaveLength(0);

    // The card's Edit action is a sibling of the card button, not a descendant.
    for (const edit of screen.getAllByTitle("Edit script content")) {
      expect(card.contains(edit)).toBe(false);
    }
  });

  it("shows the empty state for an agent with no scripts", async () => {
    render(
      <TestWrapper>
        <AgentScriptUpload agentName="gpu-node-1" registered={[]} onClose={() => {}} />
      </TestWrapper>,
    );
    expect(
      await screen.findByText("No scripts found on gpu-node-1."),
    ).toBeInTheDocument();
  });
});
