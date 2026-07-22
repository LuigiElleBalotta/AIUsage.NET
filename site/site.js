// Small GSAP-driven entrance/scroll animations for the landing page.
// Everything here is progressive enhancement: index.html and styles.css already render a fully
// readable, static page without this script (important for crawlers, no-JS users, and reduced
// motion). If GSAP fails to load (CDN blocked, offline, etc.) we bail out silently and the page
// simply stays static — no broken layout, nothing hidden.
(function () {
  if (typeof gsap === "undefined") return;

  var reduceMotion = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  if (reduceMotion) return;

  if (typeof ScrollTrigger !== "undefined") {
    gsap.registerPlugin(ScrollTrigger);
  }

  // Hero entrance: fade + rise in a short cascade.
  var heroItems = gsap.utils.toArray("[data-hero-item]");
  if (heroItems.length) {
    gsap.set(heroItems, { opacity: 0, y: 22 });
    gsap.to(heroItems, {
      opacity: 1,
      y: 0,
      duration: 0.7,
      ease: "power3.out",
      stagger: 0.08,
      delay: 0.1,
    });
  }

  // Gentle continuous float on the screenshot window, once it has landed.
  var floatEl = document.querySelector("[data-float]");
  if (floatEl) {
    gsap.to(floatEl, {
      y: -10,
      duration: 2.6,
      ease: "sine.inOut",
      repeat: -1,
      yoyo: true,
      delay: 0.9,
    });
  }

  // Scroll-triggered reveal for each below-the-fold section.
  if (typeof ScrollTrigger !== "undefined") {
    gsap.utils.toArray("[data-reveal]").forEach(function (section) {
      var items = section.querySelectorAll("[data-reveal-item]");
      var targets = items.length ? items : section.querySelector(".wrap") || section;

      gsap.set(targets, { opacity: 0, y: 26 });
      gsap.to(targets, {
        opacity: 1,
        y: 0,
        duration: 0.6,
        ease: "power2.out",
        stagger: items.length ? 0.06 : 0,
        scrollTrigger: {
          trigger: section,
          start: "top 80%",
          once: true,
        },
      });
    });
  }
})();
