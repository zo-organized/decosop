// Thin wrapper around TinyMCE for the ONLINE binder editor. TinyMCE is loaded on demand from
// wwwroot/lib so the rest of the app never pays for it, and works with no internet access.
window.onlineEditor = (function () {
    var loading = null;

    function ensureLoaded() {
        if (window.tinymce) return Promise.resolve();
        if (loading) return loading;
        loading = new Promise(function (resolve, reject) {
            var s = document.createElement('script');
            s.src = '/lib/tinymce/tinymce.min.js';
            s.onload = function () { resolve(); };
            s.onerror = function () { loading = null; reject(new Error('Could not load the editor.')); };
            document.head.appendChild(s);
        });
        return loading;
    }

    // Everything from the editor's top edge to the bottom of the window, less a small gap.
    function fitHeight(host) {
        return Math.max(300, Math.floor(window.innerHeight - host.getBoundingClientRect().top - 12));
    }

    function fitToWindow(editor, host) {
        editor.__fit = function () { editor.getContainer().style.height = fitHeight(host) + 'px'; };
        window.addEventListener('resize', editor.__fit);
        editor.__fit();
    }

    return {
        // Create the editor inside an empty host element. Blazor renders the host with no
        // children, so it never diffs against the DOM TinyMCE builds inside it.
        init: function (hostId, html) {
            return ensureLoaded().then(function () {
                var host = document.getElementById(hostId);
                if (!host) return;
                var area = document.createElement('textarea');
                area.id = hostId + '-area';
                host.appendChild(area);

                return tinymce.init({
                    target: area,
                    base_url: '/lib/tinymce',
                    suffix: '.min',
                    promotion: false,
                    branding: false,
                    // The page fills the screen below the toolbar (see fitToWindow); no menu bar,
                    // one row of small buttons like Word's, with overflow behind a "…" button.
                    height: fitHeight(host),
                    menubar: false,
                    toolbar_mode: 'sliding',
                    plugins: 'advlist autolink charmap code fullscreen image link lists searchreplace table visualblocks wordcount',
                    toolbar: 'undo redo | blocks fontsize | bold italic underline strikethrough | forecolor backcolor | ' +
                             'alignleft aligncenter alignright | bullist numlist outdent indent | ' +
                             'table hr link image charmap | removeformat | searchreplace fullscreen code',
                    table_toolbar: 'tableprops tabledelete | tableinsertrowbefore tableinsertrowafter tabledeleterow | ' +
                                   'tableinsertcolbefore tableinsertcolafter tabledeletecol | tablemergecells tablesplitcells | tablecellbackgroundcolor',
                    table_default_styles: { 'border-collapse': 'collapse', 'width': '100%' },
                    table_header_type: 'sectionCells',
                    // Pasted screenshots are kept inline in the page; there is no upload store.
                    paste_data_images: true,
                    automatic_uploads: false,
                    images_file_types: 'jpeg,jpg,jpe,png,gif,webp',
                    // Links to other SOPs are site-relative (/sops/12) and must stay that way.
                    relative_urls: false,
                    remove_script_host: true,
                    convert_urls: true,
                    // Same house style as the page view and print (margins, 10pt body), so the
                    // page is written exactly as it will look.
                    content_css: '/css/online-doc.css',
                    body_class: 'online-doc',
                    content_style: 'body { max-width: 8.5in; margin: 0 auto; padding: 0.3in 0.4in 0.3in 0.7in; box-sizing: border-box; }',
                    font_size_formats: '7pt 8pt 9pt 10pt 11pt 12pt 14pt',
                    // Wide tables can be shrunk to fit with Table properties → Class.
                    table_class_list: [
                        { title: 'Normal (10pt)', value: '' },
                        { title: 'Fit: 9pt', value: 'fit-9' },
                        { title: 'Fit: 8pt', value: 'fit-8' },
                        { title: 'Fit: 7pt', value: 'fit-7' }
                    ],
                    setup: function (editor) {
                        editor.on('init', function () {
                            editor.setContent(html || '');
                            editor.undoManager.clear();
                            editor.setDirty(false);
                            fitToWindow(editor, host);
                        });
                        editor.on('remove', function () {
                            if (editor.__fit) window.removeEventListener('resize', editor.__fit);
                        });
                    }
                });
            });
        },

        // The content can be megabytes once screenshots are pasted in — far beyond what a
        // single Blazor Server interop message may carry — so it is handed back as a stream.
        // Return the raw bytes: Blazor wraps them into the stream reference .NET asked for.
        getContent: function (hostId) {
            var ed = tinymce.get(hostId + '-area');
            var html = ed ? ed.getContent() : '';
            return new TextEncoder().encode(html);
        },

        isDirty: function (hostId) {
            var ed = window.tinymce && tinymce.get(hostId + '-area');
            return !!(ed && ed.isDirty());
        },

        markClean: function (hostId) {
            var ed = window.tinymce && tinymce.get(hostId + '-area');
            if (ed) ed.setDirty(false);
        },

        destroy: function (hostId) {
            var ed = window.tinymce && tinymce.get(hostId + '-area');
            if (ed) ed.remove();
        },

        // Ask before leaving with unsaved changes (closing the tab, reloading).
        guardUnload: function (hostId, on) {
            var key = '__onlineEditorGuard_' + hostId;
            if (window[key]) { window.removeEventListener('beforeunload', window[key]); window[key] = null; }
            if (!on) return;
            window[key] = function (e) {
                if (window.onlineEditor.isDirty(hostId)) { e.preventDefault(); e.returnValue = ''; }
            };
            window.addEventListener('beforeunload', window[key]);
        }
    };
})();
