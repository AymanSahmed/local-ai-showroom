// Draws a synthetic "scanned" claim form so the OCR + extraction demo works without real customer data.
// All names and numbers are fictional (Contoso is a Microsoft fictitious company).

export const SAMPLE_FIELDS = [
  ['Claim number', 'CL-2026-10457'],
  ['Policy number', 'MTR-88-301-552'],
  ['Policyholder', 'Mona Adel Farouk'],
  ['Phone', '+20 100 555 0199'],
  ['Date of incident', '28 October 2026, 18:40'],
  ['Location', 'Ring Road, near Exit 12, New Cairo'],
  ['Vehicle', 'Toyota Corolla 2022, plate ABC 1234'],
  ['Estimated repair cost', 'EGP 48,500'],
];

export const SAMPLE_DESCRIPTION =
  'While stopped at a traffic light, the insured vehicle was hit from behind by a delivery van. ' +
  'The rear bumper, trunk lid and left tail light are damaged. No injuries were reported. ' +
  'Police report no. 7731/2026 is attached. The policyholder requests approval to repair the car ' +
  'at the Contoso partner garage in Nasr City and a replacement car for 5 days.';

function wrap(g, text, maxWidth) {
  const words = text.split(' ');
  const lines = [];
  let line = '';
  for (const w of words) {
    const test = line ? `${line} ${w}` : w;
    if (g.measureText(test).width > maxWidth && line) {
      lines.push(line);
      line = w;
    } else {
      line = test;
    }
  }
  if (line) lines.push(line);
  return lines;
}

/** Returns a PNG data URL (1240 x 1754, A4 at 150 dpi). */
export function makeSampleScan() {
  const W = 1240;
  const H = 1754;
  const canvas = document.createElement('canvas');
  canvas.width = W;
  canvas.height = H;
  const g = canvas.getContext('2d');
  const font = '"Segoe UI", Arial, sans-serif';

  g.fillStyle = '#fbfaf6';
  g.fillRect(0, 0, W, H);

  // A slight rotation makes it look scanned.
  g.save();
  g.translate(W / 2, H / 2);
  g.rotate(-0.006);
  g.translate(-W / 2, -H / 2);

  g.fillStyle = '#1b2a4a';
  g.fillRect(90, 90, 70, 70);
  g.fillStyle = '#fbfaf6';
  g.font = `bold 40px ${font}`;
  g.fillText('C', 110, 140);

  g.fillStyle = '#1b2a4a';
  g.font = `bold 46px ${font}`;
  g.fillText('CONTOSO INSURANCE', 185, 135);
  g.font = `30px ${font}`;
  g.fillStyle = '#3f4a5c';
  g.fillText('Motor Claim Notification Form', 185, 178);

  g.strokeStyle = '#1b2a4a';
  g.lineWidth = 3;
  g.beginPath();
  g.moveTo(90, 215);
  g.lineTo(W - 90, 215);
  g.stroke();

  let y = 290;
  for (const [label, value] of SAMPLE_FIELDS) {
    g.font = `600 28px ${font}`;
    g.fillStyle = '#3f4a5c';
    g.fillText(`${label}:`, 100, y);
    g.font = `30px ${font}`;
    g.fillStyle = '#111827';
    g.fillText(value, 470, y);
    g.strokeStyle = '#c9ced8';
    g.lineWidth = 1;
    g.beginPath();
    g.moveTo(460, y + 12);
    g.lineTo(W - 100, y + 12);
    g.stroke();
    y += 72;
  }

  y += 30;
  g.font = `600 30px ${font}`;
  g.fillStyle = '#1b2a4a';
  g.fillText('Description of the incident', 100, y);
  y += 50;
  g.font = `28px ${font}`;
  g.fillStyle = '#111827';
  for (const line of wrap(g, SAMPLE_DESCRIPTION, W - 220)) {
    g.fillText(line, 100, y);
    y += 44;
  }

  y += 70;
  g.font = `28px ${font}`;
  g.fillStyle = '#3f4a5c';
  g.fillText('Signature of policyholder: ______________________', 100, y);
  g.fillText('Date: 29/10/2026', 860, y);

  // "Received" stamp
  g.save();
  g.translate(930, 1580);
  g.rotate(-0.18);
  g.strokeStyle = 'rgba(190, 30, 45, 0.75)';
  g.fillStyle = 'rgba(190, 30, 45, 0.75)';
  g.lineWidth = 4;
  g.strokeRect(-150, -48, 300, 96);
  g.font = `bold 34px ${font}`;
  g.fillText('RECEIVED', -95, -2);
  g.font = `22px ${font}`;
  g.fillText('30 OCT 2026', -68, 32);
  g.restore();

  g.restore();

  // Light speckle noise, like a photocopy.
  for (let i = 0; i < 3500; i += 1) {
    g.fillStyle = `rgba(0, 0, 0, ${Math.random() * 0.09})`;
    g.fillRect(Math.random() * W, Math.random() * H, 1.6, 1.6);
  }

  return canvas.toDataURL('image/png');
}
