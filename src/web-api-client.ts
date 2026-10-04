// Generated/adapted Web API Client for Clean Architecture
export class SwaggerException extends Error {
  status: number;
  response: string;
  headers: { [key: string]: any };
  result: any;

  constructor(message: string, status: number, response: string, headers: { [key: string]: any }, result: any) {
    super();
    this.message = message;
    this.status = status;
    this.response = response;
    this.headers = headers;
    this.result = result;
  }

  protected isSwaggerException = true;

  static isSwaggerException(obj: any): obj is SwaggerException {
    return obj.isSwaggerException === true;
  }
}

export interface LookupDto {
  id: number;
  title: string;
}

export interface ColourDto {
  code: string;
  name: string;
}

export interface TodoItemDto {
  id: number;
  listId: number;
  title: string;
  done: boolean;
  priority: number;
  note?: string | null;
}

export interface TodoListDto {
  id: number;
  title: string;
  colour: string;
  items: TodoItemDto[];
}

export interface TodosVm {
  priorityLevels: LookupDto[];
  colours: ColourDto[];
  lists: TodoListDto[];
}

export interface WeatherForecast {
  date: string;
  temperatureC: number;
  temperatureF: number;
  summary: string;
}

export interface CreateTodoListCommand {
  title: string;
  colour?: string;
}

export interface UpdateTodoListCommand {
  id?: number;
  title?: string;
  colour?: string;
}

export interface CreateTodoItemCommand {
  listId: number;
  title: string;
}

export interface UpdateTodoItemCommand {
  id: number;
  listId?: number;
  title?: string;
  done?: boolean;
}

export interface UpdateTodoItemDetailCommand {
  id: number;
  listId?: number;
  priority?: number;
  note?: string | null;
}

export interface InfoResponse {
  email: string;
  isEmailConfirmed?: boolean;
}

export class LoginRequest {
  email?: string;
  password?: string;

  constructor(data?: { email?: string; password?: string }) {
    if (data) {
      this.email = data.email;
      this.password = data.password;
    }
  }
}

export class RegisterRequest {
  email?: string;
  password?: string;

  constructor(data?: { email?: string; password?: string }) {
    if (data) {
      this.email = data.email;
      this.password = data.password;
    }
  }
}

async function request(url: string, options: RequestInit = {}): Promise<any> {
  const defaultOptions: RequestInit = {
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
    },
    ...options,
  };

  const response = await fetch(url, defaultOptions);

  if (response.status === 204) {
    return undefined;
  }

  const responseText = await response.text();

  if (!response.ok) {
    throw new SwaggerException(
      `Request failed with status ${response.status}`,
      response.status,
      responseText,
      Object.fromEntries(response.headers.entries()),
      null
    );
  }

  try {
    return responseText ? JSON.parse(responseText) : undefined;
  } catch {
    return responseText;
  }
}

export class TodoListsClient {
  async getTodoLists(): Promise<TodosVm> {
    return request('/api/TodoLists', { method: 'GET' });
  }

  async createTodoList(command: CreateTodoListCommand): Promise<number> {
    const res = await request('/api/TodoLists', {
      method: 'POST',
      body: JSON.stringify(command),
    });
    return typeof res === 'number' ? res : res?.id ?? res;
  }

  async updateTodoList(id: number, command: UpdateTodoListCommand): Promise<void> {
    await request(`/api/TodoLists/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ ...command, id }),
    });
  }

  async deleteTodoList(id: number): Promise<void> {
    await request(`/api/TodoLists/${id}`, { method: 'DELETE' });
  }
}

export class TodoItemsClient {
  async createTodoItem(command: CreateTodoItemCommand): Promise<number> {
    const res = await request('/api/TodoItems', {
      method: 'POST',
      body: JSON.stringify(command),
    });
    return typeof res === 'number' ? res : res?.id ?? res;
  }

  async updateTodoItem(id: number, command: UpdateTodoItemCommand): Promise<void> {
    await request(`/api/TodoItems/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ ...command, id }),
    });
  }

  async updateTodoItemDetail(id: number, command: UpdateTodoItemDetailCommand): Promise<void> {
    await request(`/api/TodoItems/UpdateDetail/${id}`, {
      method: 'PATCH',
      body: JSON.stringify({ ...command, id }),
    });
  }

  async deleteTodoItem(id: number): Promise<void> {
    await request(`/api/TodoItems/${id}`, { method: 'DELETE' });
  }
}

export class WeatherForecastsClient {
  async getWeatherForecasts(): Promise<WeatherForecast[]> {
    return request('/api/WeatherForecasts', { method: 'GET' });
  }
}

export class UsersClient {
  async infoGET(): Promise<InfoResponse> {
    return request('/api/Users/manage/info', { method: 'GET' });
  }

  async login(useCookies?: boolean, useSessionCookies?: boolean, body?: LoginRequest): Promise<any> {
    const params = new URLSearchParams();
    if (useCookies !== undefined) params.append('useCookies', String(useCookies));
    if (useSessionCookies !== undefined) params.append('useSessionCookies', String(useSessionCookies));
    const query = params.toString() ? `?${params.toString()}` : '';

    return request(`/api/Users/login${query}`, {
      method: 'POST',
      body: JSON.stringify(body || {}),
    });
  }

  async register(body?: RegisterRequest): Promise<void> {
    await request('/api/Users/register', {
      method: 'POST',
      body: JSON.stringify(body || {}),
    });
  }

  async logout(body?: any): Promise<void> {
    await request('/api/Users/logout', {
      method: 'POST',
      body: JSON.stringify(body || {}),
    });
  }
}
