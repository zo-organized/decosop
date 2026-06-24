window.editorInterop = {
    instance: null,
    dirty: false,

    _beforeUnloadHandler: function (e) {
        if (window.editorInterop.dirty) {
            e.preventDefault();
            e.returnValue = '';
        }
    },

    create: function (elementId, initialHtml) {
        const el = document.getElementById(elementId);
        if (!el) return;

        if (this.instance) {
            this.instance.destruct();
            this.instance = null;
        }

        this.dirty = false;

        this.instance = Jodit.make('#' + elementId, {
            height: 'calc(100vh - 14rem)',
            toolbarSticky: false,
            showCharsCounter: false,
            showWordsCounter: false,
            showXPathInStatusbar: false,
            buttons: [
                'paragraph', '|',
                'bold', 'italic', 'underline', 'strikethrough', 'superscript', 'subscript', '|',
                'ul', 'ol', '|',
                'font', 'fontsize', 'brush', '|',
                'align', 'indent', 'outdent', '|',
                'image', 'table', 'link', 'hr', 'symbols', '|',
                'copyformat', 'eraser', '|',
                'undo', 'redo', '|',
                'source', 'fullsize'
            ],
            placeholder: 'Start writing your SOP here...',
            // Preserve formatting when pasting (especially from Word) instead of stripping it.
            askBeforePasteHTML: false,
            askBeforePasteFromWord: false,
            defaultActionOnPaste: 'insert_as_html',
            defaultActionOnPasteFromWord: 'insert_as_html'
        });

        if (initialHtml) {
            this.instance.value = initialHtml;
        }

        // Track changes after initial content is set
        this.instance.events.on('change', () => {
            this.dirty = true;
        });

        window.addEventListener('beforeunload', this._beforeUnloadHandler);
    },

    getHtml: function () {
        if (!this.instance) return '';
        return this.instance.value;
    },

    clearDirty: function () {
        this.dirty = false;
    },

    dispose: function () {
        this.dirty = false;
        window.removeEventListener('beforeunload', this._beforeUnloadHandler);
        if (this.instance) {
            this.instance.destruct();
            this.instance = null;
        }
    }
};
