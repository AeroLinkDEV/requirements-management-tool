import { useEffect } from "react";
import "./PasswordVisibility.css";

const enhanced = "data-password-visibility";

export function usePasswordVisibilityControls() {
  useEffect(() => {
    const enhance = () => {
      document.querySelectorAll<HTMLInputElement>('input[type="password"]').forEach((input) => {
        if (input.hasAttribute(enhanced)) return;
        input.setAttribute(enhanced, "true");
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
        const label = input.closest("label");
        if (label?.parentElement) {
          const wrapper = document.createElement("div");
          wrapper.className = "hasPasswordVisibility";
          label.replaceWith(wrapper);
          wrapper.append(label, button);
        } else {
          input.parentElement?.classList.add("hasPasswordVisibility");
          input.insertAdjacentElement("afterend", button);
        }
      });
    };
    enhance();
    const observer = new MutationObserver(enhance);
    observer.observe(document.body, { childList: true, subtree: true });
    return () => observer.disconnect();
  }, []);
}
