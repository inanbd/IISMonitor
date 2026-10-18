// Progressive enhancements only: every link and page works without this file.
(() => {
    "use strict";

    // Screenshots open full size in a dialog instead of navigating to the image.
    let dialog;
    document.addEventListener("click", event => {
        const link = event.target.closest("a[data-zoom]");
        if (!link || event.ctrlKey || event.metaKey || event.shiftKey || typeof HTMLDialogElement !== "function")
            return;
        event.preventDefault();
        if (!dialog) {
            dialog = document.createElement("dialog");
            dialog.className = "zoom-dialog";
            dialog.addEventListener("click", () => dialog.close());
            document.body.append(dialog);
        }
        const source = link.querySelector("img");
        const image = document.createElement("img");
        image.src = link.href;
        image.alt = source ? source.alt : "";
        dialog.replaceChildren(image);
        dialog.setAttribute("aria-label", image.alt || "Screenshot");
        dialog.showModal();
    });

    // "On this page": built from the article's h2 headings, highlighting the one being read.
    const toc = document.querySelector("[data-toc]");
    const headings = [...document.querySelectorAll(".prose > h2[id]")];
    if (!toc || headings.length < 3)
        return;

    const title = document.createElement("h2");
    title.textContent = "On this page";
    const list = document.createElement("ol");
    const links = headings.map(heading => {
        const item = document.createElement("li");
        const link = document.createElement("a");
        link.href = "#" + heading.id;
        link.textContent = heading.textContent;
        item.append(link);
        list.append(item);
        return link;
    });
    toc.append(title, list);
    toc.hidden = false;

    // The current section is the last heading scrolled past the sticky header.
    let scheduled = false;
    const highlight = () => {
        scheduled = false;
        let current = headings[0];
        for (const heading of headings) {
            if (heading.getBoundingClientRect().top < 140)
                current = heading;
        }
        links.forEach((link, i) => headings[i] === current ? link.setAttribute("aria-current", "location") : link.removeAttribute("aria-current"));
    };
    addEventListener("scroll", () => {
        if (!scheduled) {
            scheduled = true;
            requestAnimationFrame(highlight);
        }
    }, { passive: true });
    highlight();
})();
