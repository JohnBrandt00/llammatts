"use strict";

/* Minimal Lua code editor: textarea with a syntax-highlighted backdrop, line numbers,
   Tab inserts spaces, Ctrl+S = save, Ctrl+Enter = run. No dependencies. */

const LUA_KEYWORDS = new Set(("and break do else elseif end false for function goto if in local nil not or " +
  "repeat return then true until while").split(" "));
const LUA_BUILTINS = new Set(("print pairs ipairs tostring tonumber type select pcall xpcall error assert next " +
  "string table math os unpack rawget rawset setmetatable getmetatable " +
  "tts speakers audio log json app").split(" "));

function escHtml(s) {
  return s.replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
}

function highlightLua(src) {
  const re = /(--\[(=*)\[[\s\S]*?(?:\]\2\]|$))|(--[^\n]*)|(\[(=*)\[[\s\S]*?(?:\]\5\]|$))|("(?:\\.|[^"\\\n])*"?|'(?:\\.|[^'\\\n])*'?)|(\b(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b)|([A-Za-z_][A-Za-z0-9_]*)/g;
  let out = "", last = 0, m;
  while ((m = re.exec(src)) !== null) {
    out += escHtml(src.slice(last, m.index));
    const t = m[0];
    let cls = null;
    if (m[1] || m[3]) cls = "lc";          // comments
    else if (m[4] || m[6]) cls = "ls";     // strings
    else if (m[7]) cls = "ln";             // numbers
    else if (m[8]) {
      if (LUA_KEYWORDS.has(t)) cls = "lk";
      else if (LUA_BUILTINS.has(t)) cls = "lb";
    }
    out += cls ? `<span class="${cls}">${escHtml(t)}</span>` : escHtml(t);
    last = m.index + t.length;
  }
  out += escHtml(src.slice(last));
  return out + "\n";
}

class LuaEditor {
  constructor(container, { onSave, onRun } = {}) {
    container.classList.add("luaed");
    container.innerHTML = `
      <div class="luaed-gutter"></div>
      <div class="luaed-wrap">
        <pre class="luaed-hl" aria-hidden="true"></pre>
        <textarea class="luaed-input" spellcheck="false" autocomplete="off" autocapitalize="off"></textarea>
      </div>`;
    this.gutter = container.querySelector(".luaed-gutter");
    this.hl = container.querySelector(".luaed-hl");
    this.input = container.querySelector(".luaed-input");

    this.input.addEventListener("input", () => this.render());
    this.input.addEventListener("scroll", () => {
      this.hl.scrollTop = this.input.scrollTop;
      this.hl.scrollLeft = this.input.scrollLeft;
      this.gutter.scrollTop = this.input.scrollTop;
    });
    this.input.addEventListener("keydown", (e) => {
      if (e.key === "Tab") {
        e.preventDefault();
        const { selectionStart: s, selectionEnd: epos, value } = this.input;
        this.input.value = value.slice(0, s) + "    " + value.slice(epos);
        this.input.selectionStart = this.input.selectionEnd = s + 4;
        this.render();
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
        e.preventDefault();
        onSave && onSave();
      } else if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
        e.preventDefault();
        onRun && onRun();
      }
    });
    this.render();
  }

  setValue(v) {
    this.input.value = v;
    this.render();
  }

  getValue() {
    return this.input.value;
  }

  render() {
    const src = this.input.value;
    this.hl.innerHTML = highlightLua(src);
    const lines = src.split("\n").length;
    if (this._lines !== lines) {
      this._lines = lines;
      this.gutter.innerHTML = Array.from({ length: lines }, (_, i) => i + 1).join("\n");
    }
  }
}
