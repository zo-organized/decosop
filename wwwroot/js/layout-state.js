// Small helpers for LayoutState and the header's user menu. Choices live in cookies (not
// localStorage) so the server knows them when it first draws the page.
window.decoLayout = {
    setCookie: function (name, value) {
        var maxAge = value ? 60 * 60 * 24 * 365 * 5 : 0;   // empty value = forget it
        document.cookie = name + '=' + encodeURIComponent(value || '') + '; path=/; max-age=' + maxAge + '; SameSite=Lax';
    }
};
