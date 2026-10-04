export function Home() {
  return (
    <div>
      <h1>Welcome</h1>
      <p>A full-stack application with a <a href="https://react.dev/">React</a> frontend, built with:</p>
      <ul>
        <li><a href="https://nodejs.org/">Node.js</a> and <a href="https://expressjs.com/">Express</a> for backend API services</li>
        <li><a href="https://react.dev/">React 19</a> and <a href="https://vite.dev/">Vite</a> for client-side code</li>
        <li><a href="https://picocss.com/">Pico CSS</a> for semantic styling and theme switching</li>
        <li><a href="https://lucide.dev/">Lucide</a> for icons</li>
      </ul>
      <p>To help you get started:</p>
      <ul>
        <li><strong>Client-side navigation</strong>. Click <em>Counter</em>, <em>Weather</em>, or <em>Tasks</em> in the navbar.</li>
        <li><strong>Protected areas</strong>. Weather and Tasks require signing in. Use the default credentials <code>administrator@localhost</code> / <code>Administrator1!</code> or register a new user.</li>
        <li><strong>Theme toggle</strong>. Switch between light, dark, and system color themes with the sun/moon icon.</li>
      </ul>
    </div>
  );
}
