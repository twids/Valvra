"use strict";
(() => {
    const supported = {sv: "sv-SE", en: "en-GB"};
    let catalogs = {};
    const cookie = document.cookie.split(";").map(x => x.trim()).find(x => x.startsWith("Valvra.Language="))?.slice("Valvra.Language=".length);
    const browserLanguage = (navigator.languages || [navigator.language]).map(x => x?.split("-")[0].toLowerCase()).find(x => Object.hasOwn(supported, x));
    let language = Object.hasOwn(supported, cookie) ? cookie : browserLanguage || "sv";
    const lookup = (code, key) => Object.hasOwn(catalogs[code] || {}, key) ? catalogs[code][key] : undefined;
    const format = (key, values = {}) => (lookup(language, key) ?? lookup("sv", key) ?? key)
        .replace(/\{(\w+)\}/g, (token, name) => Object.hasOwn(values, name) ? String(values[name]) : token);
    class Message {
        constructor(key, values) { this.key = key; this.values = values || {}; }
        toString() { return format(this.key, this.values); }
    }
    function setText(element, text) {
        element.textContent = text;
        if (text instanceof Message) {
            element.setAttribute("data-i18n", text.key);
            element.setAttribute("data-i18n-values", JSON.stringify(text.values));
        } else { element.removeAttribute("data-i18n"); element.removeAttribute("data-i18n-values"); }
    }
    function translateDom() {
        document.documentElement.lang = language;
        document.querySelectorAll("[data-i18n]").forEach(element => {
            element.textContent = format(element.getAttribute("data-i18n"), JSON.parse(element.getAttribute("data-i18n-values") || "{}"));
        });
        for (const attribute of ["aria-label", "placeholder"]) {
            document.querySelectorAll("[data-i18n-" + attribute + "]").forEach(element =>
                element.setAttribute(attribute, format(element.getAttribute("data-i18n-" + attribute))));
        }
        document.querySelectorAll("[data-language-picker]").forEach(picker => picker.value = language);
    }
    function setLanguage(value) {
        if (!Object.hasOwn(supported, value)) return false;
        language = value;
        // This cookie contains only a language code. Never store vault content here.
        document.cookie = "Valvra.Language=" + value + "; Path=/; Max-Age=31536000; SameSite=Lax" + (location.protocol === "https:" ? "; Secure" : "");
        translateDom();
        document.dispatchEvent(new CustomEvent("valvra:languagechange"));
        return true;
    }
    function validation(input) {
        if (input.validity.valueMissing) return format("Fyll i det obligatoriska fältet.");
        if (input.validity.rangeUnderflow || input.validity.rangeOverflow || input.validity.stepMismatch) return format("Ange ett värde inom det tillåtna intervallet.");
        return format("Ange ett giltigt värde.");
    }
    const ready = Promise.all(["sv", "en"].map(async code => {
        const response = await fetch("/i18n/" + code + ".json", {credentials: "same-origin", cache: "no-store"});
        if (!response.ok) throw new Error("Could not load language resources / Kunde inte läsa språkfiler.");
        catalogs[code] = await response.json();
    })).then(() => {
        translateDom();
        document.querySelectorAll("[data-language-picker]").forEach(picker => picker.addEventListener("change", () => setLanguage(picker.value)));
    });
    window.ValvraI18n = {message: (key, values) => new Message(key, values), setText, setLanguage, translateDom, validation, ready,
        get language() { return language; }, get locale() { return supported[language]; }};
})();
