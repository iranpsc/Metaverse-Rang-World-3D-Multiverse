const { spawn } = require('child_process');
const http = require('http');

function makeRequest(method, path, body, callback) {
  const options = {
    hostname: 'localhost',
    port: 3000,
    path,
    method,
    headers: { 'Content-Type': 'application/json' }
  };

  const req = http.request(options, (res) => {
    let data = '';
    res.on('data', chunk => data += chunk);
    res.on('end', () => callback(null, res.statusCode, data));
  });

  req.on('error', e => callback(e));
  if (body) req.write(JSON.stringify(body));
  req.end();
}

const envName = 'myEnv_abc123';
const posId = 'pos_7k9m2x';

function waitForServer(retries = 10, delay = 1000) {
  return new Promise((resolve, reject) => {
    let attempts = 0;
    const check = () => {
      attempts++;
      http.get({ hostname: 'localhost', port: 3000, path: '/api/create-env', timeout: 1000 }, (res) => {
        resolve();
      }).on('error', () => {
        if (attempts >= retries) reject(new Error('Server not starting'));
        else setTimeout(check, delay);
      });
    };
    check();
  });
}

async function runTests() {
  try {
    await waitForServer();
    console.log('Server is up!');
  } catch (e) {
    console.error('Server not available:', e.message);
    return;
  }

  // Test 1: Create environment
  makeRequest('POST', `/api/create-env`, { environmentName: envName }, (err, status, data) => {
    if (err) console.error('Test 1 error:', err);
    else console.log(`Test 1 - Create env: status=${status}, body=${data}`);
    
    // Test 2: Try creating same environment (should fail)
    makeRequest('POST', `/api/create-env`, { environmentName: envName }, (err, status, data) => {
      if (err) console.error('Test 2 error:', err);
      else console.log(`Test 2 - Create duplicate: status=${status}, body=${data}`);
      
      // Test 3: Add position
      makeRequest('POST', `/api/add-position`, {
        environmentName: envName,
        positionId: posId,
        position: { x: 12.45, y: 0.0, z: -8.32 },
        rotation: { x: 0, y: 0.707, z: 0, w: 0.707 }
      }, (err, status, data) => {
        if (err) console.error('Test 3 error:', err);
        else console.log(`Test 3 - Add position: status=${status}, body=${data}`);
        
        // Test 4: Get position
        makeRequest('GET', `/api/get-position?env=${envName}&spawn=${posId}`, {}, (err, status, data) => {
          if (err) console.error('Test 4 error:', err);
          else console.log(`Test 4 - Get position: status=${status}, body=${data}`);
          
          // Test 5: List positions
          makeRequest('GET', `/api/list-positions?env=${envName}`, {}, (err, status, data) => {
            if (err) console.error('Test 5 error:', err);
            else console.log(`Test 5 - List positions: status=${status}, body=${data}`);
            
            // Test 6: Update position
            makeRequest('PUT', `/api/update-position`, {
              environmentName: envName,
              positionId: posId,
              position: { x: 10.0, y: 5.0, z: 2.0 },
              rotation: { x: 0, y: 1, z: 0, w: 0 }
            }, (err, status, data) => {
              if (err) console.error('Test 6 error:', err);
              else console.log(`Test 6 - Update position: status=${status}, body=${data}`);
              
              // Test 7: Get updated position
              makeRequest('GET', `/api/get-position?env=${envName}&spawn=${posId}`, {}, (err, status, data) => {
                if (err) console.error('Test 7 error:', err);
                else console.log(`Test 7 - Get updated: status=${status}, body=${data}`);
                
                // Test 8: List positions again
                makeRequest('GET', `/api/list-positions?env=${envName}`, {}, (err, status, data) => {
                  if (err) console.error('Test 8 error:', err);
                  else console.log(`Test 8 - List after update: status=${status}, body=${data}`);
                  console.log('All tests completed!');
                  process.exit(0);
                });
              });
            });
          });
        });
      });
    });
  });
}

runTests();