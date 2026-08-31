import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, act, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Models from "../features/models";

/**
 * Test-chat drawer suite: the chat button on /Models rows opens a right-side
 * drawer wired to client.sendTestChat — the "test a model + connection" flow.
 */

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

/** Renders the Models page and clicks the row's test-chat button. */
async function openTestChat(modelName = "llama-3.1-70b") {
  const user = userEvent.setup();
  render(
    <TestWrapper>
      <Models />
    </TestWrapper>,
  );
  await waitFor(() => {
    expect(screen.getByText(modelName)).toBeInTheDocument();
  });
  await user.click(
    screen.getByRole("button", { name: new RegExp(`Test chat with ${modelName}`) }),
  );
  return user;
}

function mockTurn(overrides: Partial<{
  content: string;
  reasoning: string | null;
  latencyMs: number;
  promptTokens: number | null;
  completionTokens: number | null;
}> = {}) {
  return {
    content: "Hello! Connection works.",
    reasoning: null,
    latencyMs: 1200,
    promptTokens: 10,
    completionTokens: 4,
    ...overrides,
  };
}

describe("Models page — test chat", () => {
  it("opens the right-side chat drawer from a managed model row", async () => {
    await openTestChat();

    const drawer = await screen.findByRole("dialog");
    // Drawer header carries the model identity (scoped — the row shows it too)
    expect(within(drawer).getByText("Llama · 70B · Q4_K_M")).toBeInTheDocument();
    expect(within(drawer).getByTestId("test-chat-quick-prompts")).toBeInTheDocument();
  });

  it("sends a message and renders the reply with observed stats", async () => {
    const spy = vi
      .spyOn(mockClient, "sendTestChat")
      .mockResolvedValue(mockTurn({ latencyMs: 1500, completionTokens: 4 }));

    const user = await openTestChat();
    await user.type(await screen.findByLabelText("Message"), "ping");
    await user.click(screen.getByRole("button", { name: "Send message" }));

    await waitFor(() => {
      expect(screen.getByText("Hello! Connection works.")).toBeInTheDocument();
    });

    // Stats row (scoped to the drawer — benchmark chips elsewhere also show tok/s)
    const drawer = screen.getByRole("dialog");
    expect(within(drawer).getByText(/1,?500ms/)).toBeInTheDocument();
    expect(within(drawer).getByText(/2\.7 tok\/s/)).toBeInTheDocument(); // 4 tokens / 1.5s
    expect(within(drawer).getByText(/10 prompt/)).toBeInTheDocument();
    expect(within(drawer).getByText(/4 out/)).toBeInTheDocument();

    // The call went through with full conversation history
    expect(spy).toHaveBeenCalledTimes(1);
    const [modelId, messages] = spy.mock.calls[0];
    expect(modelId).toBe("1"); // llama-3.1-70b
    expect(messages[messages.length - 1]).toEqual({ role: "user", content: "ping" });
  });

  it("streams deltas into the transcript via onDelta", async () => {
    vi.spyOn(mockClient, "sendTestChat").mockImplementation(
      (_modelId, _messages, opts) =>
        new Promise((resolve) => {
          act(() => {
            opts?.onDelta?.({ content: "streamed " });
            opts?.onDelta?.({ content: "reply" });
          });
          resolve(mockTurn({ content: "streamed reply" }));
        }),
    );

    const user = await openTestChat();
    await user.type(await screen.findByLabelText("Message"), "hi");
    await user.click(screen.getByRole("button", { name: "Send message" }));

    await waitFor(() => {
      expect(screen.getByText(/streamed reply/)).toBeInTheDocument();
    });
  });

  it("renders reasoning text in a collapsible Thinking section", async () => {
    vi.spyOn(mockClient, "sendTestChat").mockResolvedValue(
      mockTurn({
        content: "The answer.",
        reasoning: "Let me think about this carefully.",
      }),
    );

    const user = await openTestChat();
    await user.type(await screen.findByLabelText("Message"), "q");
    await user.click(screen.getByRole("button", { name: "Send message" }));

    const thinking = await screen.findByText("Thinking");
    expect(thinking).toBeInTheDocument();
    // <details> content is collapsed but present in the DOM
    expect(screen.getByText(/Let me think about this carefully./)).toBeInTheDocument();
  });

  it("shows an error banner when the request fails", async () => {
    vi.spyOn(mockClient, "sendTestChat").mockRejectedValue(
      Object.assign(new Error("Upstream returned 503"), { name: "ApiError" }),
    );

    const user = await openTestChat();
    await user.type(await screen.findByLabelText("Message"), "hello?");
    await user.click(screen.getByRole("button", { name: "Send message" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Request failed");
    expect(alert).toHaveTextContent("Upstream returned 503");
  });

  it("keeps the partial reply and marks it stopped when Stop is pressed", async () => {
    let rejectFn: ((err: Error) => void) | undefined;
    vi.spyOn(mockClient, "sendTestChat").mockImplementation(
      (_modelId, _messages, opts) =>
        new Promise((_resolve, reject) => {
          rejectFn = (err) => reject(err);
          act(() => {
            opts?.onDelta?.({ content: "partial answer" });
          });
          opts?.signal?.addEventListener("abort", () => {
            rejectFn?.(new DOMException("Aborted", "AbortError"));
          });
        }),
    );

    const user = await openTestChat();
    await user.type(await screen.findByLabelText("Message"), "long question");
    await user.click(screen.getByRole("button", { name: "Send message" }));

    // Composer switches to Stop while pending
    const stopBtn = await screen.findByRole("button", { name: "Stop generating" });
    expect(screen.getByText(/partial answer/)).toBeInTheDocument();

    await user.click(stopBtn);

    await waitFor(() => {
      expect(screen.getByText("(stopped)")).toBeInTheDocument();
    });
    expect(screen.getByText(/partial answer/)).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("quick-prompt chips send immediately", async () => {
    const spy = vi
      .spyOn(mockClient, "sendTestChat")
      .mockResolvedValue(mockTurn());

    const user = await openTestChat();
    await user.click(
      screen.getByRole("button", { name: "What model are you?" }),
    );

    await waitFor(() => {
      expect(spy).toHaveBeenCalledTimes(1);
    });
    const [, messages] = spy.mock.calls[0];
    expect(messages[messages.length - 1]).toEqual({
      role: "user",
      content: "What model are you?",
    });
  });

  it("clear resets the conversation for the current model", async () => {
    vi.spyOn(mockClient, "sendTestChat").mockResolvedValue(mockTurn());

    const user = await openTestChat();
    await user.type(await screen.findByLabelText("Message"), "one");
    await user.click(screen.getByRole("button", { name: "Send message" }));
    await screen.findByText("Hello! Connection works.");

    await user.click(screen.getByRole("button", { name: "Clear conversation" }));

    await waitFor(() => {
      expect(screen.queryByText("Hello! Connection works.")).not.toBeInTheDocument();
    });
    expect(screen.getByTestId("test-chat-quick-prompts")).toBeInTheDocument();
  });

  it("cloud rows have a working test-chat button too", async () => {
    const user = await openTestChatViaCloud();

    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeInTheDocument();
    });
    void user;
  });

  it("disables the chat button for invalid models", async () => {
    const models = [
      {
        id: "inv1",
        name: "broken-model",
        family: "Broken",
        parameterSize: "7B",
        quantization: "Q8",
        status: "invalid" as const,
        lastBenchmark: null,
        contextWindow: 4096,
        containerImage: "test/broken",
        sourceRuntimeId: null,
        sourceRuntimeName: null,
        sourceRuntimeAgent: null,
        origin: "swarm",
        providerName: null,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
    ];
    vi.spyOn(mockClient, "listModels").mockResolvedValueOnce(models);

    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("broken-model")).toBeInTheDocument();
    });

    const btn = screen.getByRole("button", { name: /Test chat with broken-model/ });
    expect(btn).toBeDisabled();
  });

  it("conversation survives closing and reopening the drawer", async () => {
    vi.spyOn(mockClient, "sendTestChat").mockResolvedValue(mockTurn());

    const user = await openTestChat();
    await user.type(await screen.findByLabelText("Message"), "remember me");
    await user.click(screen.getByRole("button", { name: "Send message" }));
    await screen.findByText("Hello! Connection works.");

    // Close via Escape, then reopen from the same row
    await user.keyboard("{Escape}");
    await waitFor(() => {
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /Test chat with llama-3\.1-70b/ }));
    await waitFor(() => {
      expect(screen.getByRole("dialog")).toBeInTheDocument();
      expect(screen.getByText("Hello! Connection works.")).toBeInTheDocument();
    });
  });
});

async function openTestChatViaCloud() {
  const user = userEvent.setup();
  render(
    <TestWrapper>
      <Models />
    </TestWrapper>,
  );
  await waitFor(() => {
    expect(screen.getByRole("tab", { name: /Cloud/i })).toBeInTheDocument();
  });
  await user.click(screen.getByRole("tab", { name: /Cloud/i }));
  await waitFor(() => {
    expect(screen.getByText("gpt-4o")).toBeInTheDocument();
  });
  await user.click(screen.getByRole("button", { name: "Test chat with gpt-4o" }));
  return user;
}
