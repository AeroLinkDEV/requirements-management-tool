import { useEffect } from "react";
import "./PasswordVisibility.css";

const enhanced = "data-password-visibility";

export function usePasswordVisibilityControls() {
  useEffect(() => {
    const enhance = () => {
      document.querySelectorAll<HTMLInputElement>('input[type="password"]').forEach((input) => {
        if (input.hasAttribute(enhanced)) return;
        input.setAttribute(enhanced, "true");
        // These fields are labelled by wrapping — `<label>New password<input/></label>` — so every descendant
        // of the label contributes to the field's accessible name. Adding a button inside it renamed the field
        // to "New password Reveal typed characters" for anyone using a screen reader. Pinning the name the
        // label already provided, before the button is inserted, keeps the field called what it is called.
        const labelled = input.parentElement?.textContent?.trim();
        if (labelled && !input.hasAttribute("aria-label")) input.setAttribute("aria-label", labelled);
        input.parentElement?.classList.add("hasPasswordVisibility");
        const button = document.createElement("button");
        button.type = "button";
        button.className = "passwordVisibility";
        button.setAttribute("aria-label", "Reveal typed characters");
        button.title = "Show password";
        button.setAttribute("aria-pressed", "false");
        button.textContent = "◎";
        button.addEventListener("click", () => {
          const visible = input.type === "text";
          input.type = visible ? "password" : "text";
          button.setAttribute("aria-label", visible ? "Reveal typed characters" : "Conceal typed characters");
          button.title = visible ? "Show password" : "Hide password";
          button.setAttribute("aria-pressed", String(!visible));
          button.textContent = visible ? "◎" : "◉";
        });
        input.insertAdjacentElement("afterend", button);
      });
    };
    enhance();
    const observer = new MutationObserver(enhance);
    observer.observe(document.body, { childList: true, subtree: true });
    return () => observer.disconnect();
  }, []);
}
