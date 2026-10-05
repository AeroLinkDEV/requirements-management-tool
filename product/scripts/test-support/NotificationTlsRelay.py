# Shared disposable qualification peer for backend and supported restore owners; never a product relay.
import ssl, socket, sys, json, os, time, threading
root, reply = sys.argv[1:3]
port = int(sys.argv[3]) if len(sys.argv) > 3 else 0
timeout = float(sys.argv[4]) if len(sys.argv) > 4 else 30
connections = int(sys.argv[5]) if len(sys.argv) > 5 else 1
evidence_lock = threading.Lock()
def fact(event, **values):
  with evidence_lock:
    with open(os.path.join(root, 'events.jsonl'), 'a', encoding='utf-8') as evidence:
        evidence.write(json.dumps(dict(event=event, **values)) + '\n'); evidence.flush(); os.fsync(evidence.fileno())
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(os.path.join(root, 'certificate.pem'), os.path.join(root, 'key.pem'))
listener = socket.socket(); listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); listener.bind(('127.0.0.1', port)); listener.listen(16); listener.settimeout(timeout)
print(listener.getsockname()[1], flush=True)
def send(s, value): s.sendall(value.encode('ascii'))
def line(s):
    value = bytearray()
    while True:
        b = s.recv(1)
        if not b: raise EOFError()
        if b == b'\n': return value.decode('ascii').rstrip('\r')
        value.extend(b)
        if len(value) > 65536: raise ValueError('fixture line bound')
def session(raw):
  tls, data, wire, envelope = False, 0, '', ''
  try:
    with raw:
        fact('TcpAccepted'); raw.settimeout(timeout); send(raw, '220 owned fixture\r\n')
        assert line(raw).startswith('EHLO '); fact('GreetingAccepted')
        if reply == 'NoStartTls':
            send(raw, '250 owned fixture\r\n'); line(raw); return
        send(raw, '250-owned fixture\r\n250 STARTTLS\r\n')
        assert line(raw) == 'STARTTLS'; send(raw, '220 Ready\r\n'); tls = True; fact('TlsStarted')
        with context.wrap_socket(raw, server_side=True) as secure:
            assert line(secure).startswith('EHLO ')
            send(secure, '250-owned fixture\r\n250 AUTH PLAIN\r\n' if reply in ('AuthAccept', 'AuthRefuse') else '250 owned fixture\r\n')
            while True:
                command = line(secure)
                if command.startswith('AUTH '):
                    fact('AuthAttempted'); send(secure, '535 Invalid credentials\r\n' if reply == 'AuthRefuse' else '235 Authenticated\r\n')
                    if reply == 'AuthAccept': fact('Authenticated')
                elif command.startswith('MAIL FROM:'):
                    envelope += command + '\n'; send(secure, '250 Sender\r\n')
                elif command.startswith('RCPT TO:'):
                    envelope += command + '\n'
                    send(secure, '451 Later\r\n' if reply == 'Refuse451' else '550 Refused\r\n' if reply == 'Refuse550' else '250 Recipient\r\n')
                elif command == 'DATA':
                    fact('DataCommandReceived')
                    if reply == 'PauseBeforeDataReply':
                        time.sleep(timeout); raise TimeoutError()
                    send(secure, '354 Send\r\n'); parts = []
                    while True:
                        content = line(secure)
                        if content == '.': break
                        parts.append(content + '\r\n')
                    wire = ''.join(parts); data += 1; fact('DataReceived', wire=wire, envelope=envelope)
                    if reply == 'LoseFinalReply': break
                    if reply == 'PauseAfterData':
                        until = time.monotonic() + timeout
                        while not os.path.exists(os.path.join(root, 'release-data')):
                            if time.monotonic() >= until: raise TimeoutError()
                            time.sleep(.02)
                    send(secure, '250 Accepted\r\n'); fact('FinalDataAccepted')
                elif command == 'RSET': send(secure, '250 Reset\r\n')
                elif command == 'QUIT': print('QUIT', flush=True); break
                else: raise ValueError('unexpected command')
  except (ssl.SSLError, OSError, EOFError): pass
  finally:
    print(json.dumps(dict(tls=tls, data=data, wire=wire, envelope=envelope)), flush=True)
threads = []
try:
    for index in range(connections):
        raw = listener.accept()[0]
        if connections == 1: session(raw)
        else:
            thread = threading.Thread(target=session, args=(raw,), daemon=True); thread.start(); threads.append(thread)
except (OSError, EOFError): pass
finally:
    listener.close()
    for thread in threads: thread.join(timeout)
