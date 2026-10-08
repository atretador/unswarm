import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  InputModalitiesField,
  normalizeInputModalities,
} from "../features/models/input-modalities-field";
import { TestWrapper } from "./test-utils";

describe("normalizeInputModalities", () => {
  it("defaults to text-only for missing or null input", () => {
    expect(normalizeInputModalities()).toEqual(["text"]);
    expect(normalizeInputModalities(null)).toEqual(["text"]);
    expect(normalizeInputModalities([])).toEqual(["text"]);
  });

  it("lowercases, trims, dedupes, and reorders to the canonical order", () => {
    expect(
      normalizeInputModalities([" PDF ", "image", "IMAGE", "audio", "text", "image"]),
    ).toEqual(["text", "image", "audio", "pdf"]);
  });

  it("always adds text even when absent", () => {
    expect(normalizeInputModalities(["image"])).toEqual(["text", "image"]);
  });

  it("drops unknown tokens", () => {
    expect(normalizeInputModalities(["image", "bogus"])).toEqual(["text", "image"]);
  });
});

describe("InputModalitiesField", () => {
  it("renders the locked text chip and toggle chips", () => {
    render(
      <TestWrapper>
        <InputModalitiesField value={["text"]} onChange={() => {}} />
      </TestWrapper>,
    );

    expect(screen.getByRole("group", { name: "Input Modalities" })).toBeInTheDocument();
    // text is a locked chip, not a button
    expect(screen.queryByRole("button", { name: "Text" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Image" })).toHaveAttribute(
      "aria-pressed",
      "false",
    );
  });

  it("adds a modality when toggled on and reports canonical order", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(
      <TestWrapper>
        <InputModalitiesField value={["text"]} onChange={onChange} />
      </TestWrapper>,
    );

    await user.click(screen.getByRole("button", { name: "PDF" }));
    expect(onChange).toHaveBeenCalledWith(["text", "pdf"]);
  });

  it("removes a modality when toggled off", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(
      <TestWrapper>
        <InputModalitiesField value={["text", "image"]} onChange={onChange} />
      </TestWrapper>,
    );

    expect(screen.getByRole("button", { name: "Image" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    await user.click(screen.getByRole("button", { name: "Image" }));
    expect(onChange).toHaveBeenCalledWith(["text"]);
  });

  it("disables the toggles when disabled", () => {
    render(
      <TestWrapper>
        <InputModalitiesField value={["text"]} onChange={() => {}} disabled />
      </TestWrapper>,
    );

    for (const name of ["Image", "Video", "Audio", "PDF"]) {
      expect(screen.getByRole("button", { name })).toBeDisabled();
    }
  });
});
