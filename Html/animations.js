(() => {
    "use strict";

    const interactiveSelector = [
        ".btn",
        ".checkbox-wrapper",
        ".img-card",
        ".history-item",
        ".url-tag"
    ].join(",");

    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

    function isUnavailable(element) {
        return element.matches(":disabled") || element.getAttribute("aria-disabled") === "true";
    }

    function removeRipple(ripple, host) {
        ripple.remove();

        if (!host.querySelector(":scope > .click-ripple")) {
            host.classList.remove("click-effect-host");
        }
    }

    function showClickEffect(host, clientX, clientY) {
        if (reducedMotion.matches || isUnavailable(host)) {
            return;
        }

        const rect = host.getBoundingClientRect();
        const hasPointerPosition = Number.isFinite(clientX) && Number.isFinite(clientY)
            && (clientX !== 0 || clientY !== 0);
        const x = hasPointerPosition ? clientX - rect.left : rect.width / 2;
        const y = hasPointerPosition ? clientY - rect.top : rect.height / 2;
        const diameter = Math.max(rect.width, rect.height) * 1.45;
        const ripple = document.createElement("span");

        host.querySelectorAll(":scope > .click-ripple").forEach(existing => existing.remove());
        host.classList.add("click-effect-host");

        ripple.className = "click-ripple";
        ripple.setAttribute("aria-hidden", "true");
        ripple.style.width = `${diameter}px`;
        ripple.style.height = `${diameter}px`;
        ripple.style.left = `${x}px`;
        ripple.style.top = `${y}px`;

        host.appendChild(ripple);
        ripple.addEventListener("animationend", () => removeRipple(ripple, host), { once: true });
    }

    document.addEventListener("pointerdown", event => {
        if (event.button !== 0 || !(event.target instanceof Element)) {
            return;
        }

        const host = event.target.closest(interactiveSelector);
        if (!(host instanceof HTMLElement)) {
            return;
        }

        showClickEffect(host, event.clientX, event.clientY);
    }, { passive: true });

    document.addEventListener("keydown", event => {
        if ((event.key !== "Enter" && event.key !== " ") || !(event.target instanceof HTMLElement)) {
            return;
        }

        const host = event.target.closest(interactiveSelector);
        if (host instanceof HTMLElement) {
            showClickEffect(host, 0, 0);
        }
    });
})();
