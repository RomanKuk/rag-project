import { Pipe, PipeTransform, inject } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { marked, Renderer } from 'marked';
import DOMPurify from 'dompurify';

const renderer = new Renderer();

renderer.code = ({ text, lang }: { text: string; lang?: string }) => {
  const label = lang || 'code';
  const escaped = text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
  return `<div class="code-block">
  <div class="code-header">
    <span class="code-lang">${label}</span>
    <button class="copy-btn" data-copy="${escaped.replace(/"/g, '&quot;')}">
      <span class="copy-label">Copy</span>
    </button>
  </div>
  <pre><code class="language-${label}">${escaped}</code></pre>
</div>`;
};

marked.use({ renderer });

@Pipe({ name: 'markdown', standalone: true })
export class MarkdownPipe implements PipeTransform {
  private readonly sanitizer = inject(DomSanitizer);

  transform(value: string | null | undefined): SafeHtml {
    if (!value) return '';
    const raw   = marked.parse(value, { async: false, breaks: true }) as string;
    const clean = DOMPurify.sanitize(raw, {
      ADD_ATTR: ['data-copy'],
    });
    return this.sanitizer.bypassSecurityTrustHtml(clean);
  }
}
