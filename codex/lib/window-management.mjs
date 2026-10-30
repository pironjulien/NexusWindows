// Pixels d'écran Windows ; validation avant la pause et le jeton, puis à nouveau dans le helper natif.
export const WINDOW_ACTIONS = ['restore', 'minimize', 'maximize', 'close', 'bottom', 'front', 'move', 'resize'];
export function windowRequest(action, options = {}) {
  const refuse = message => { throw Object.assign(new Error(message + '; nothing was sent'), { beforeInput: true }); };
  if (!WINDOW_ACTIONS.includes(action) || action === 'close') refuse('Unsupported window action');
  const request = { action };
  for (const [key, value] of Object.entries(options)) {
    if (!['x', 'y', 'width', 'height', 'foreground'].includes(key)) refuse('Unknown window argument ' + key);
    if (value === undefined) continue;
    if (key === 'foreground') {
      if (typeof value !== 'boolean') refuse('foreground must be boolean');
    } else if (!Number.isInteger(value) || value < -2147483648 || value > 2147483647) refuse(key + ' must be an integer screen pixel value');
    request[key] = value;
  }
  if (action === 'front' && request.foreground !== true) refuse('front requires foreground:true');
  if (action === 'move' && (!Object.hasOwn(request, 'x') || !Object.hasOwn(request, 'y'))) refuse('move requires x and y');
  if (action === 'resize' && !(request.width > 0 && request.height > 0)) refuse('resize requires positive width and height');
  if (action !== 'move' && ('x' in request || 'y' in request) || action !== 'resize' && ('width' in request || 'height' in request))
    refuse('Coordinates belong to move and dimensions to resize');
  return request;
}
