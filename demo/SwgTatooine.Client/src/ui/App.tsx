import { Chat } from './Chat';
import { Hud } from './Hud';
import { Inspector } from './Inspector';
import { Toolbar } from './Toolbar';
import { Viewport } from './Viewport';

export function App() {
  return (
    <div className="app">
      <Viewport />
      <Toolbar />
      <Hud />
      <Chat />
      <Inspector />
    </div>
  );
}
