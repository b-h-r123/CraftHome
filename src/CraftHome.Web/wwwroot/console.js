(() => {
    const key = "crafthome-theme";
    const root = document.documentElement;
    const system = matchMedia("(prefers-color-scheme: dark)");
    const savedTheme = () => {
        try { return localStorage.getItem(key); }
        catch { return null; }
    };
    let preference = savedTheme();
    const currentTheme = () =>
        preference === "dark" || preference === "light" ? preference : system.matches ? "dark" : "light";
    const applyTheme = () => {
        const theme = currentTheme();
        root.dataset.bsTheme = theme;
        document.querySelectorAll("[data-theme-toggle]").forEach(button =>
            button.setAttribute("aria-pressed", String(theme === "dark")));
    };
    applyTheme();

    document.addEventListener("DOMContentLoaded", () => {
        applyTheme();
        Blazor.addEventListener("enhancedload", applyTheme);
    });
    system.addEventListener("change", applyTheme);
    window.addEventListener("storage", event => {
        if (event.key === key || event.key === null) {
            preference = savedTheme();
            applyTheme();
        }
    });

    document.addEventListener("click", async event => {
        if (event.target.closest("[data-theme-toggle]")) {
            preference = currentTheme() === "dark" ? "light" : "dark";
            applyTheme();
            try { localStorage.setItem(key, preference); }
            catch { /* The choice still applies to this page. */ }
        }
        const button = event.target.closest("[data-copy-address]");
        if (!button) return;
        const panel = button.closest(".join-panel");
        const input = panel.querySelector("input");
        const status = panel.querySelector("[role=status]");
        if (navigator.clipboard?.writeText) {
            try {
                await navigator.clipboard.writeText(input.value);
                status.textContent = "已复制连接地址。";
                return;
            } catch { /* Select the address when clipboard access is denied. */ }
        }
        input.focus();
        input.select();
        let copied = false;
        try { copied = document.execCommand("copy"); }
        catch { /* Keep the address selected for manual copying. */ }
        if (copied) button.focus();
        status.textContent = copied ? "已复制连接地址。" : "已选中地址，可按 Ctrl+C，或长按选择复制。";
    });
})();
