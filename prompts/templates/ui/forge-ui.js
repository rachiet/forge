/* Forge UI kit — the behaviour the components need.

   Harness-owned. Forge rewrites this file into the repo on every task run, so an
   edit made here is discarded on the next one.

   Everything is driven by data attributes and wired on DOMContentLoaded, so a
   page written as plain HTML gets working modals, tabs, menus and carousels
   without any application JavaScript. The one thing a page calls directly is
   fg.toast(). All handlers are delegated from document, so markup added later
   by the application works without being registered. */

(function (global) {
  "use strict";

  /* Every element matching a selector, as a real array. */
  function all(selector, root) {
    return Array.prototype.slice.call((root || document).querySelectorAll(selector));
  }

  /* ---------------------------------------------------------------- modal */

  /* Shows the modal with this id and moves focus into it. */
  function openModal(id) {
    var modal = document.getElementById(id);
    if (!modal) return;
    modal.hidden = false;
    document.body.style.overflow = "hidden";
    var focusable = modal.querySelector("input, select, textarea, button, [href]");
    if (focusable) focusable.focus();
  }

  /* Hides the modal with this id and releases the page scroll. */
  function closeModal(id) {
    var modal = document.getElementById(id);
    if (!modal) return;
    modal.hidden = true;
    document.body.style.overflow = "";
  }

  /* Clicking the backdrop — but not the dialog itself — closes the modal. */
  function onModalBackdropClick(event) {
    var modal = event.target.closest(".fg-modal");
    if (modal && event.target === modal) closeModal(modal.id);
  }

  /* Escape closes the topmost open modal, which is what every user expects. */
  function onKeyDown(event) {
    if (event.key !== "Escape") return;
    var open = all(".fg-modal").filter(function (m) { return !m.hidden; });
    if (open.length) closeModal(open[open.length - 1].id);
  }

  /* ---------------------------------------------------------------- toast */

  /* Shows a transient message in the bottom-right corner.
     message — the text to show.
     kind    — "success", "danger", or omitted for a neutral note. */
  function toast(message, kind) {
    var toaster = document.querySelector(".fg-toaster");
    if (!toaster) {
      toaster = document.createElement("div");
      toaster.className = "fg-toaster";
      document.body.appendChild(toaster);
    }

    var node = document.createElement("div");
    node.className = "fg-toast" + (kind ? " fg-toast--" + kind : "");
    node.setAttribute("role", "status");
    node.textContent = message;
    toaster.appendChild(node);

    global.setTimeout(function () {
      node.classList.add("fg-toast--leaving");
      global.setTimeout(function () { node.remove(); }, 220);
    }, 3200);
  }

  /* ---------------------------------------------------------------- tabs */

  /* Activates one tab in a tab strip and shows the panel it names. */
  function selectTab(tab) {
    var strip = tab.closest(".fg-tabs");
    if (!strip) return;

    all(".fg-tabs__tab", strip).forEach(function (other) {
      var active = other === tab;
      other.classList.toggle("fg-tabs__tab--active", active);
      other.setAttribute("aria-selected", active ? "true" : "false");
      var panel = document.getElementById(other.getAttribute("data-fg-panel"));
      if (panel) panel.hidden = !active;
    });
  }

  /* ---------------------------------------------------------------- menu */

  /* Opens this menu's list and closes every other one on the page. */
  function toggleMenu(trigger) {
    var list = trigger.parentElement.querySelector(".fg-menu__list");
    if (!list) return;
    var opening = list.hidden;
    closeAllMenus();
    list.hidden = !opening;
  }

  function closeAllMenus() {
    all(".fg-menu__list").forEach(function (list) { list.hidden = true; });
  }

  /* ---------------------------------------------------------------- carousel */

  /* Scrolls a carousel by one slide. direction is 1 for next, -1 for previous. */
  function scrollCarousel(arrow, direction) {
    var carousel = arrow.closest(".fg-carousel");
    var track = carousel && carousel.querySelector(".fg-carousel__track");
    if (!track) return;
    var slide = track.querySelector(".fg-carousel__slide");
    var step = slide ? slide.getBoundingClientRect().width + 16 : track.clientWidth;
    track.scrollBy({ left: step * direction, behavior: "smooth" });
  }

  /* Greys out an arrow that would do nothing, so the ends of the strip are visible. */
  function updateCarouselArrows(carousel) {
    var track = carousel.querySelector(".fg-carousel__track");
    var prev = carousel.querySelector(".fg-carousel__arrow--prev");
    var next = carousel.querySelector(".fg-carousel__arrow--next");
    if (!track) return;
    if (prev) prev.disabled = track.scrollLeft <= 2;
    if (next) next.disabled = track.scrollLeft + track.clientWidth >= track.scrollWidth - 2;
  }

  /* ---------------------------------------------------------------- wiring */

  /* One delegated click handler for every component, so markup rendered after
     load behaves the same as markup that was in the page to begin with. */
  function onClick(event) {
    var target = event.target;

    var opener = target.closest("[data-fg-open]");
    if (opener) { openModal(opener.getAttribute("data-fg-open")); return; }

    var closer = target.closest("[data-fg-close]");
    if (closer) {
      var named = closer.getAttribute("data-fg-close");
      var modal = named ? document.getElementById(named) : closer.closest(".fg-modal");
      if (modal) closeModal(modal.id);
      return;
    }

    var tab = target.closest(".fg-tabs__tab");
    if (tab) { selectTab(tab); return; }

    var menuTrigger = target.closest("[data-fg-menu]");
    if (menuTrigger) { toggleMenu(menuTrigger); return; }

    var prevArrow = target.closest(".fg-carousel__arrow--prev");
    if (prevArrow) { scrollCarousel(prevArrow, -1); return; }

    var nextArrow = target.closest(".fg-carousel__arrow--next");
    if (nextArrow) { scrollCarousel(nextArrow, 1); return; }

    closeAllMenus();
    onModalBackdropClick(event);
  }

  function ready() {
    document.addEventListener("click", onClick);
    document.addEventListener("keydown", onKeyDown);

    all(".fg-carousel").forEach(function (carousel) {
      var track = carousel.querySelector(".fg-carousel__track");
      if (!track) return;
      updateCarouselArrows(carousel);
      track.addEventListener("scroll", function () { updateCarouselArrows(carousel); });
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", ready);
  } else {
    ready();
  }

  /* The public surface a page may call. */
  global.fg = {
    openModal: openModal,
    closeModal: closeModal,
    toast: toast,
  };
})(window);
