import express, { Request, Response, NextFunction } from 'express';
import cookieParser from 'cookie-parser';
import cors from 'cors';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const app = express();
let PORT = process.env.PORT ? parseInt(process.env.PORT, 10) : 3000;
const portArgIdx = process.argv.indexOf('--port');
if (portArgIdx !== -1 && process.argv[portArgIdx + 1]) {
  const parsedPort = parseInt(process.argv[portArgIdx + 1], 10);
  if (!isNaN(parsedPort)) {
    PORT = parsedPort;
  }
}

app.use(cors({ origin: true, credentials: true }));
app.use(express.json());
app.use(cookieParser());

// ── In-Memory Data Store ───────────────────────────────────────────────────────

interface User {
  email: string;
  passwordHash: string;
}

const users = new Map<string, User>([
  ['administrator@localhost', { email: 'administrator@localhost', passwordHash: 'Administrator1!' }],
]);

interface TodoItem {
  id: number;
  listId: number;
  title: string;
  done: boolean;
  priority: number;
  note: string | null;
}

interface TodoList {
  id: number;
  title: string;
  colour: string;
}

const priorityLevels = [
  { id: 0, title: 'None' },
  { id: 1, title: 'Low' },
  { id: 2, title: 'Medium' },
  { id: 3, title: 'High' },
];

const colours = [
  { code: '#78909C', name: 'Grey' },
  { code: '#AB47BC', name: 'Purple' },
  { code: '#5C6BC0', name: 'Blue' },
  { code: '#26A69A', name: 'Teal' },
  { code: '#4CAF50', name: 'Green' },
  { code: '#D98B2B', name: 'Orange' },
  { code: '#E05C4D', name: 'Red' },
];

let nextListId = 2;
let nextItemId = 5;

const lists: TodoList[] = [
  { id: 1, title: 'Tasks', colour: '#4CAF50' },
];

const items: TodoItem[] = [
  { id: 1, listId: 1, title: 'Make a todo list 📃', done: true, priority: 0, note: '' },
  { id: 2, listId: 1, title: 'Check off the first item ✅', done: true, priority: 0, note: '' },
  { id: 3, listId: 1, title: "Realise you've already done two things on the list! 🤯", done: false, priority: 1, note: '' },
  { id: 4, listId: 1, title: 'Reward yourself with a nice, long nap 🏆', done: false, priority: 2, note: '' },
];

// ── Auth Helper ────────────────────────────────────────────────────────────────

function getCurrentUser(req: Request): User | null {
  const email = req.cookies?.ca_auth;
  if (email && users.has(email)) {
    return users.get(email)!;
  }
  const authHeader = req.headers.authorization;
  if (authHeader && authHeader.startsWith('Bearer ')) {
    const tokenEmail = authHeader.substring(7);
    if (users.has(tokenEmail)) {
      return users.get(tokenEmail)!;
    }
  }
  return null;
}

function requireAuth(req: Request, res: Response, next: NextFunction): void {
  const user = getCurrentUser(req);
  if (!user) {
    res.status(401).json({ title: 'Unauthorized', status: 401 });
    return;
  }
  next();
}

// ── Auth / Users API ───────────────────────────────────────────────────────────

app.get(['/api/Users/manage/info', '/api/Users/info', '/api/manage/info'], (req: Request, res: Response) => {
  const user = getCurrentUser(req);
  if (!user) {
    res.status(401).json({ title: 'Unauthorized', status: 401 });
    return;
  }
  res.json({ email: user.email, isEmailConfirmed: true });
});

app.post('/api/Users/login', (req: Request, res: Response) => {
  const { email, password } = req.body || {};
  if (!email || !password) {
    res.status(400).json({ errors: { General: ['Email and password are required.'] } });
    return;
  }

  const user = users.get(email.toLowerCase());
  if (!user || user.passwordHash !== password) {
    res.status(400).json({ errors: { General: ['Invalid email or password.'] } });
    return;
  }

  res.cookie('ca_auth', user.email, {
    httpOnly: false,
    path: '/',
    sameSite: 'lax',
    maxAge: 7 * 24 * 60 * 60 * 1000,
  });

  res.json({
    tokenType: 'Bearer',
    accessToken: user.email,
    expiresIn: 604800,
    refreshToken: user.email,
  });
});

app.post('/api/Users/register', (req: Request, res: Response) => {
  const { email, password } = req.body || {};
  if (!email || !password) {
    res.status(400).json({
      errors: {
        Email: !email ? ['Email is required.'] : [],
        Password: !password ? ['Password is required.'] : [],
      },
    });
    return;
  }

  const lowerEmail = email.toLowerCase();
  if (users.has(lowerEmail)) {
    res.status(400).json({ errors: { Email: ['User already exists.'] } });
    return;
  }

  users.set(lowerEmail, { email: lowerEmail, passwordHash: password });
  res.status(200).json({});
});

app.post('/api/Users/logout', (_req: Request, res: Response) => {
  res.clearCookie('ca_auth', { path: '/' });
  res.status(200).json({});
});

// ── Weather Forecasts API ──────────────────────────────────────────────────────

const summaries = ['Freezing', 'Bracing', 'Chilly', 'Cool', 'Mild', 'Warm', 'Balmy', 'Hot', 'Sweltering', 'Scorching'];

app.get(['/api/WeatherForecasts', '/weatherforecast', '/WeatherForecast'], requireAuth, (_req: Request, res: Response) => {
  const forecasts = Array.from({ length: 5 }, (_, i) => {
    const d = new Date();
    d.setDate(d.getDate() + i + 1);
    const tempC = Math.floor(Math.random() * 75) - 20;
    const tempF = 32 + Math.floor(tempC / 0.5556);
    const summary = summaries[Math.floor(Math.random() * summaries.length)];
    return {
      date: d.toISOString(),
      temperatureC: tempC,
      temperatureF: tempF,
      summary,
    };
  });
  res.json(forecasts);
});

// ── Todo Lists API ─────────────────────────────────────────────────────────────

app.get('/api/TodoLists', requireAuth, (_req: Request, res: Response) => {
  const responseLists = lists.map(l => ({
    id: l.id,
    title: l.title,
    colour: l.colour,
    items: items
      .filter(i => i.listId === l.id)
      .map(i => ({
        id: i.id,
        listId: i.listId,
        title: i.title,
        done: i.done,
        priority: i.priority,
        note: i.note,
      })),
  }));

  res.json({
    priorityLevels,
    colours,
    lists: responseLists,
  });
});

app.post('/api/TodoLists', requireAuth, (req: Request, res: Response) => {
  const { title, colour } = req.body || {};
  if (!title || !title.trim()) {
    res.status(400).json({
      errors: {
        Title: ['Title is required.'],
      },
    });
    return;
  }

  const id = nextListId++;
  const newList: TodoList = {
    id,
    title: title.trim(),
    colour: colour || '#4CAF50',
  };
  lists.push(newList);
  res.status(201).json(id);
});

app.put('/api/TodoLists/:id', requireAuth, (req: Request, res: Response) => {
  const id = Number(req.params.id);
  const { title, colour } = req.body || {};
  const list = lists.find(l => l.id === id);

  if (!list) {
    res.status(404).json({ error: 'List not found' });
    return;
  }

  if (title !== undefined) list.title = title.trim();
  if (colour !== undefined) list.colour = colour;

  res.status(204).send();
});

app.delete('/api/TodoLists/:id', requireAuth, (req: Request, res: Response) => {
  const id = Number(req.params.id);
  const index = lists.findIndex(l => l.id === id);
  if (index !== -1) {
    lists.splice(index, 1);
    for (let i = items.length - 1; i >= 0; i--) {
      if (items[i].listId === id) items.splice(i, 1);
    }
  }
  res.status(204).send();
});

// ── Todo Items API ─────────────────────────────────────────────────────────────

app.post('/api/TodoItems', requireAuth, (req: Request, res: Response) => {
  const { title, listId } = req.body || {};
  if (!title || !title.trim()) {
    res.status(400).json({ errors: { Title: ['Title is required.'] } });
    return;
  }

  const id = nextItemId++;
  const newItem: TodoItem = {
    id,
    listId: Number(listId),
    title: title.trim(),
    done: false,
    priority: 0,
    note: '',
  };
  items.push(newItem);
  res.status(201).json(id);
});

app.put('/api/TodoItems/:id', requireAuth, (req: Request, res: Response) => {
  const id = Number(req.params.id);
  const { title, done } = req.body || {};
  const item = items.find(i => i.id === id);

  if (!item) {
    res.status(404).json({ error: 'Item not found' });
    return;
  }

  if (title !== undefined) item.title = title;
  if (done !== undefined) item.done = Boolean(done);

  res.status(204).send();
});

app.patch('/api/TodoItems/UpdateDetail/:id', requireAuth, (req: Request, res: Response) => {
  const id = Number(req.params.id);
  const { listId, priority, note } = req.body || {};
  const item = items.find(i => i.id === id);

  if (!item) {
    res.status(404).json({ error: 'Item not found' });
    return;
  }

  if (listId !== undefined) item.listId = Number(listId);
  if (priority !== undefined) item.priority = Number(priority);
  if (note !== undefined) item.note = note;

  res.status(204).send();
});

app.delete('/api/TodoItems/:id', requireAuth, (req: Request, res: Response) => {
  const id = Number(req.params.id);
  const index = items.findIndex(i => i.id === id);
  if (index !== -1) {
    items.splice(index, 1);
  }
  res.status(204).send();
});

// ── Vite / Frontend Integration ────────────────────────────────────────────────

async function startServer() {
  const isProduction = process.env.NODE_ENV === 'production';

  if (!isProduction) {
    const { createServer: createViteServer } = await import('vite');
    const vite = await createViteServer({
      server: { middlewareMode: true, hmr: false },
      appType: 'spa',
    });
    app.use(vite.middlewares);
  } else {
    const distPath = path.resolve(__dirname, 'dist');
    app.use(express.static(distPath));
    app.get('*', (_req: Request, res: Response) => {
      res.sendFile(path.resolve(distPath, 'index.html'));
    });
  }

  app.listen(PORT, '0.0.0.0', () => {
    console.log(`\n  VITE ready in 150 ms\n\n  ➜  Local:   http://localhost:${PORT}/\n  ➜  Network: http://0.0.0.0:${PORT}/\n`);
  });
}

startServer().catch(err => {
  console.error('Failed to start server:', err);
  process.exit(1);
});
