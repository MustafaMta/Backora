// Backora V2 — sign-in page.
// 1) A real-time render of the Backora ziggurat (WebGL ray-marching): polished obsidian with platinum inlays and sapphire light on a
//    mirror floor, soft shadows, light from the shrine door and a beam above it. The camera follows the pointer and
//    the shrine brightens while you type. Falls back to the SVG drawing when WebGL is unavailable.
// 2) Sign-in details: show/hide password, Caps Lock warning, busy state on submit.
// Decorative and local only: nothing is sent anywhere. Honours prefers-reduced-motion (one still frame).
(function () {
  'use strict';

  var VS = 'attribute vec2 a;void main(){gl_Position=vec4(a,0.0,1.0);}';
  var FS = [
    'precision highp float;',
    'uniform vec2 uRes;uniform float uTime;uniform vec2 uPtr;uniform float uGlow;uniform float uIntro;uniform float uExpo;',
    'const vec3 DOOR=vec3(0.0,2.27,0.36);',
    'const vec3 WARM=vec3(0.48,0.56,1.0);',
    'const vec3 LKEY=vec3(-0.5728,0.6683,0.4774);',
    'vec3 gShrine;',
    'float hash(vec2 p){return fract(sin(dot(p,vec2(12.9898,78.233)))*43758.5453);}',
    'float sdBox(vec3 p,vec3 b){vec3 q=abs(p)-b;return length(max(q,0.0))+min(max(q.x,max(q.y,q.z)),0.0);}',
    'float tier(vec3 p,float cy,vec3 b){vec3 q=p-vec3(0.0,cy,0.0);float k=1.0-0.075*(clamp(q.y,-b.y,b.y)+b.y);return sdBox(q,vec3(b.x*k,b.y,b.z*k))*0.9;}',
    'float stairs(vec2 q,float ts,float hs,float n){float k=clamp(floor(q.x/ts),0.0,n-1.0);float d=100000.0;',
    '  for(int i=-1;i<=1;i++){float kk=clamp(k+float(i),0.0,n-1.0);vec2 c=vec2((kk+0.5)*ts,(kk+1.0)*hs*0.5);vec2 b=vec2(0.5*ts,(kk+1.0)*hs*0.5);vec2 dd=abs(q-c)-b;d=min(d,length(max(dd,0.0))+min(max(dd.x,dd.y),0.0));}',
    '  return d;}',
    'vec2 opU(vec2 a,vec2 b){return a.x<b.x?a:b;}',
    'vec2 mapObj(vec3 p){',
    '  float bb=sdBox(p-vec3(0.0,1.38,0.825),vec3(2.66,1.41,2.76));if(bb>0.25)return vec2(bb,1.0);',
    '  vec2 r=vec2(tier(p,0.42,vec3(2.6,0.42,1.9)),1.0);',
    '  r=opU(r,vec2(tier(p,1.18,vec3(1.85,0.34,1.3)),2.0));',
    '  r=opU(r,vec2(tier(p,1.80,vec3(1.15,0.28,0.8)),3.0));',
    '  float sh=sdBox(p-vec3(0.0,2.38,0.0),vec3(0.5,0.3,0.38));',
    '  sh=max(sh,-sdBox(p-vec3(0.0,2.27,0.40),vec3(0.11,0.19,0.07)));',
    '  sh=min(sh,sdBox(p-vec3(0.0,2.715,0.0),vec3(0.57,0.035,0.45)));',
    '  r=opU(r,vec2(sh,4.0));',
    '  r=opU(r,vec2(sdBox(p-vec3(0.0,2.27,0.335),vec3(0.11,0.19,0.01)),7.0));',
    '  float st=max(stairs(vec2(3.55-p.z,p.y),0.12643,0.06,14.0),abs(p.x)-0.3);',
    '  float rc=clamp((3.55-p.z)/1.77,0.0,1.0);',
    '  st=min(st,max(sdBox(vec3(abs(p.x)-0.36,p.y-0.45,p.z-2.665),vec3(0.06,0.46,0.885)),(p.y-0.84*rc-0.08)*0.85));',
    '  st=min(st,max(stairs(vec2(1.78-p.z,p.y-0.84),0.056,0.068,10.0),abs(p.x)-0.2));',
    '  vec3 q=vec3(abs(p.x),p.y,p.z);',
    '  st=min(st,max(stairs(vec2(2.55-q.x,q.y),0.13125,0.0525,16.0),abs(q.z-2.03)-0.27));',
    '  r=opU(r,vec2(st,5.0));',
    '  float gate=sdBox(p-vec3(0.0,1.0,1.62),vec3(0.45,0.16,0.2));',
    '  gate=max(gate,-sdBox(p-vec3(0.0,0.97,1.62),vec3(0.14,0.14,0.3)));',
    '  r=opU(r,vec2(gate,8.0));',
    '  return r;}',
    'vec2 map(vec3 p){return opU(mapObj(p),vec2(p.y,6.0));}',
    'vec3 calcN(vec3 p){vec2 e=vec2(0.0015,-0.0015);return normalize(e.xyy*map(p+e.xyy).x+e.yyx*map(p+e.yyx).x+e.yxy*map(p+e.yxy).x+e.xxx*map(p+e.xxx).x);}',
    'vec2 march(vec3 ro,vec3 rd,float tmax){float t=0.0;float id=-1.0;for(int i=0;i<150;i++){vec2 h=map(ro+rd*t);if(h.x<0.0008*t+0.0004){id=h.y;break;}t+=h.x*0.9;if(t>tmax)break;}return vec2(t,id);}',
    'vec2 marchObj(vec3 ro,vec3 rd,float tmax){float t=0.0;float id=-1.0;for(int i=0;i<90;i++){vec2 h=mapObj(ro+rd*t);if(h.x<0.001*t+0.0006){id=h.y;break;}t+=h.x*0.9;if(t>tmax)break;}return vec2(t,id);}',
    'float shadow(vec3 ro,vec3 rd){float res=1.0;float t=0.03;for(int i=0;i<40;i++){float h=mapObj(ro+rd*t).x;res=min(res,9.0*h/t);t+=clamp(h,0.02,0.3);if(res<0.002||t>12.0)break;}return clamp(res,0.0,1.0);}',
    'float ao(vec3 p,vec3 n){float o=0.0;float s=1.0;for(int i=0;i<5;i++){float h=0.02+0.12*float(i);float d=map(p+n*h).x;o+=(h-d)*s;s*=0.72;}return clamp(1.0-2.0*o,0.0,1.0);}',
    'vec3 env(vec3 r){vec3 c=vec3(0.010,0.011,0.015);',
    '  c+=vec3(0.62,0.64,0.70)*smoothstep(0.7,0.95,r.y)*0.3;',
    '  c+=vec3(0.62,0.68,0.85)*smoothstep(0.88,0.98,dot(r,normalize(vec3(-1.0,0.3,0.55))))*0.75;',
    '  c+=WARM*smoothstep(0.92,0.99,dot(r,normalize(vec3(1.0,0.2,-0.4))))*0.35;',
    '  c+=WARM*pow(max(0.0,1.0-abs(r.y)),8.0)*0.05;return c;}',
    'vec3 bgCol(vec3 rd){vec3 c=vec3(0.006,0.007,0.009);float g=max(dot(rd,gShrine),0.0);c+=WARM*(pow(g,9.0)*0.05+pow(g,70.0)*0.06);return c;}',
    'bool isGold(vec3 p,vec3 n,float id){',
    '  if(id<3.5){float top=id<1.5?0.84:(id<2.5?1.52:2.08);float bot=id<1.5?0.0:(id<2.5?0.84:1.52);',
    '    if(n.y<0.6){if(p.y>top-0.04)return true;if(abs(p.y-(bot+top)*0.5)<0.011)return true;}return false;}',
    '  if(id<4.5){if(p.y>2.678)return true;if(p.z>0.37&&abs(p.x)<0.165&&p.y<2.51&&p.y>2.08&&(abs(p.x)>0.11||p.y>2.46))return true;return false;}',
    '  if(id>7.5){return p.y>1.13&&n.y<0.6;}',
    '  return false;}',
    'vec3 shade(vec3 p,vec3 n,vec3 rd,float id,bool full){',
    '  if(id>6.5&&id<7.5)return mix(WARM,vec3(1.0),0.5)*(2.4+3.0*uGlow);',
    '  float s=full?shadow(p+n*0.004,LKEY):1.0;float a=full?ao(p,n):1.0;',
    '  vec3 V=-rd;vec3 R=reflect(rd,n);vec3 H=normalize(LKEY+V);',
    '  float ndl=max(dot(n,LKEY),0.0);float fres=pow(1.0-max(dot(n,V),0.0),5.0);',
    '  vec3 lp=DOOR+vec3(0.0,0.0,0.45);vec3 ld=lp-p;float dl=length(ld);ld/=dl;',
    '  float att=(1.0+2.4*uGlow)/(1.0+dl*dl*2.2);float ndp=max(dot(n,ld),0.0);',
    '  vec3 col;',
    '  if(isGold(p,n,id)){vec3 g=vec3(0.80,0.84,1.0);',
    '    col=g*(0.03+ndl*s*0.3+ndp*att*0.9);col+=g*env(R)*1.5;col+=g*pow(max(dot(n,H),0.0),60.0)*s*1.1;}',
    '  else{vec3 alb=vec3(0.010,0.011,0.014);',
    '    col=alb*(0.3+ndl*s*1.2)+alb*WARM*ndp*att*14.0;',
    '    col+=env(R)*mix(0.04,1.0,fres)*(0.3+0.7*a);',
    '    col+=vec3(0.85,0.9,1.0)*pow(max(dot(n,H),0.0),160.0)*s*0.55;}',
    '  return col*mix(0.3,1.0,a);}',
    'vec3 shadeGround(vec3 p,vec3 rd,float t){vec3 n=vec3(0.0,1.0,0.0);',
    '  float a=ao(p,n);float s=shadow(p+n*0.01,LKEY);',
    '  vec3 col=vec3(0.006,0.0065,0.008)*(0.45+0.55*s);',
    '  vec2 g=abs(fract(p.xz*0.5)-0.5);float w=0.006+0.0012*t;',
    '  float line=1.0-smoothstep(0.0,w,min(g.x,g.y)*2.0-0.0);',
    '  col+=WARM*line*0.035*exp(-length(p.xz)*0.11);',
    '  vec3 ld=DOOR+vec3(0.0,0.0,0.45)-p;float dl=length(ld);',
    '  col+=WARM*max(ld.y/dl,0.0)*(0.9+2.2*uGlow)/(1.0+dl*dl*1.3)*0.05;',
    '  vec3 rr=reflect(rd,n);vec2 h=marchObj(p+n*0.01,rr,16.0);',
    '  float fres=0.04+0.96*pow(1.0-max(dot(n,-rd),0.0),5.0);',
    '  vec3 refl=bgCol(rr);',
    '  if(h.y>0.0){vec3 q=p+rr*h.x;refl=shade(q,calcN(q),rr,h.y,false);}',
    '  col+=refl*mix(0.16,0.55,fres)*exp(-length(p.xz)*0.045);',
    '  return col*mix(0.4,1.0,a);}',
    'void main(){',
    '  vec2 uv=(gl_FragCoord.xy-0.5*uRes)/uRes.y;uv.y+=0.105;',
    '  float az=0.62+0.12*sin(uTime*0.045)+uPtr.x*0.24;float el=0.17-uPtr.y*0.05;',
    '  float rad=mix(26.0,19.5,uIntro);vec3 ta=vec3(0.0,1.15,0.5);',
    '  vec3 ro=ta+rad*vec3(cos(el)*sin(az),sin(el),cos(el)*cos(az));',
    '  vec3 ww=normalize(ta-ro);vec3 uu=normalize(cross(ww,vec3(0.0,1.0,0.0)));vec3 vv=cross(uu,ww);',
    '  vec3 rd=normalize(uv.x*uu+uv.y*vv+1.6*ww);',
    '  gShrine=normalize(DOOR-ro);',
    '  vec2 h=march(ro,rd,60.0);vec3 col;float tHit=h.y<0.0?1000.0:h.x;',
    '  if(h.y<0.0)col=bgCol(rd);',
    '  else{vec3 p=ro+rd*h.x;if(h.y>5.5&&h.y<6.5)col=shadeGround(p,rd,h.x);else col=shade(p,calcN(p),rd,h.y,true);',
    '    col=mix(col,bgCol(rd),1.0-exp(-0.0006*h.x*h.x));}',
    '  vec3 dp=DOOR+vec3(0.0,0.0,0.18);float tc=max(dot(dp-ro,rd),0.0);',
    '  if(tc<tHit+0.3){float dm=length(ro+rd*tc-dp);col+=WARM*(0.045+0.11*uGlow)/(0.02+dm*dm*2.2)*0.1;}',
    '  vec2 o=ro.xz;vec2 d=rd.xz;float tb=-dot(o,d)/max(dot(d,d),0.0001);',
    '  if(tb>0.0&&tb<tHit){vec3 pb=ro+rd*tb;float rr=length(pb.xz);',
    '    float f=(exp(-rr*rr*90.0)*0.28+exp(-rr*rr*7.0)*0.05)*exp(-(pb.y-2.75)*0.2)*smoothstep(2.74,3.1,pb.y);',
    '    col+=mix(WARM,vec3(1.0),0.35)*f*(0.75+0.7*uGlow);}',
    '  col*=uExpo*1.3;',
    '  col=(col*(2.51*col+0.03))/(col*(2.43*col+0.59)+0.14);',
    '  col=pow(clamp(col,0.0,1.0),vec3(0.4545));',
    '  vec2 q=gl_FragCoord.xy/uRes;col*=0.5+0.5*pow(16.0*q.x*q.y*(1.0-q.x)*(1.0-q.y),0.16);',
    '  col+=(hash(gl_FragCoord.xy+fract(uTime*7.0)*91.0)-0.5)*0.012;',
    '  gl_FragColor=vec4(col,1.0);}'
  ].join('\n');

  var current = null;

  function boot() {
    var root = document.querySelector('[data-az]');
    var auth = document.querySelector('.app-auth');
    if (current && current.root === root && current.auth === auth) return;
    if (current) { current.destroy(); current = null; }
    if (auth) current = create(root, auth);
  }

  function create(root, auth) {
    var reduce = window.matchMedia ? matchMedia('(prefers-reduced-motion: reduce)').matches : false;
    var cleanups = [];
    function on(el, type, fn, opts) { if (!el) return; el.addEventListener(type, fn, opts); cleanups.push(function () { el.removeEventListener(type, fn, opts); }); }

    // ── Sign-in details ──────────────────────────────────
    [].slice.call(auth.querySelectorAll('[data-pw-toggle]')).forEach(function (btn) {
      var input = document.getElementById(btn.getAttribute('aria-controls'));
      if (!input) return;
      btn.hidden = false;
      on(btn, 'click', function () {
        var show = input.type === 'password';
        input.type = show ? 'text' : 'password';
        btn.setAttribute('aria-pressed', show ? 'true' : 'false');
        btn.setAttribute('aria-label', show ? 'Hide password' : 'Show password');
        input.focus();
      });
    });
    var caps = auth.querySelector('[data-capslock]');
    function capsCheck(e) { if (caps && e.getModifierState) caps.hidden = !(e.target && e.target.type !== 'email' && e.target.matches && e.target.matches('input') && e.getModifierState('CapsLock')); }
    on(auth, 'keydown', capsCheck); on(auth, 'keyup', capsCheck);
    on(auth, 'focusout', function () { if (caps) caps.hidden = true; });
    on(auth, 'submit', function (e) {
      var b = e.target && e.target.querySelector('button[type="submit"]');
      if (b) { b.classList.add('is-busy'); b.setAttribute('aria-busy', 'true'); }
      glowKick = 1;
    });

    // ── Render ───────────────────────────────────────────
    var canvas = root && root.querySelector('[data-az-gl]');
    var gl = null, prog = null, U = {}, raf = 0, alive = true, started = 0, scale = 0.6, frameMs = [], lastT = 0;
    var ptr = { x: 0, y: 0 }, tp = { x: 0, y: 0 }, glow = 0, glowKick = 0, focused = false;

    function compile(type, src) {
      var s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) { console.warn('Backora sign-in render unavailable:', gl.getShaderInfoLog(s)); return null; }
      return s;
    }
    function init() {
      try { gl = canvas.getContext('webgl', { antialias: false, alpha: false, depth: false, stencil: false, powerPreference: 'high-performance' }); } catch (e) { gl = null; }
      if (!gl) return false;
      var v = compile(gl.VERTEX_SHADER, VS), f = compile(gl.FRAGMENT_SHADER, FS);
      if (!v || !f) return false;
      prog = gl.createProgram(); gl.attachShader(prog, v); gl.attachShader(prog, f); gl.linkProgram(prog);
      if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) return false;
      gl.useProgram(prog);
      var buf = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
      var a = gl.getAttribLocation(prog, 'a'); gl.enableVertexAttribArray(a); gl.vertexAttribPointer(a, 2, gl.FLOAT, false, 0, 0);
      ['uRes', 'uTime', 'uPtr', 'uGlow', 'uIntro', 'uExpo'].forEach(function (n) { U[n] = gl.getUniformLocation(prog, n); });
      return true;
    }
    function size() {
      var w = canvas.clientWidth, h = canvas.clientHeight;
      if (!w || !h) return false;
      var k = Math.min(window.devicePixelRatio || 1, 2) * scale;
      var cw = Math.max(1, Math.round(w * k)), ch = Math.max(1, Math.round(h * k));
      if (canvas.width !== cw || canvas.height !== ch) { canvas.width = cw; canvas.height = ch; }
      gl.viewport(0, 0, cw, ch);
      return true;
    }
    function ease(x) { x = Math.min(1, Math.max(0, x)); return 1 - Math.pow(1 - x, 3); }
    function draw(now) {
      if (!size()) return false;
      var t = reduce ? 30 : (now - started) / 1000;
      gl.uniform2f(U.uRes, canvas.width, canvas.height);
      gl.uniform1f(U.uTime, t);
      gl.uniform2f(U.uPtr, ptr.x, ptr.y);
      gl.uniform1f(U.uGlow, glow);
      gl.uniform1f(U.uIntro, reduce ? 1 : ease(t / 3.2));
      gl.uniform1f(U.uExpo, reduce ? 1 : ease(t / 1.6));
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      return true;
    }
    function frame(now) {
      if (!alive) return;
      raf = requestAnimationFrame(frame);
      if (document.hidden) return;
      ptr.x += (tp.x - ptr.x) * 0.045; ptr.y += (tp.y - ptr.y) * 0.045;
      var target = Math.max(focused ? 0.45 : 0, glowKick);
      glow += (target - glow) * 0.06; glowKick *= 0.965;
      if (!draw(now)) return;
      if (lastT) {
        frameMs.push(now - lastT);
        if (frameMs.length >= 24) {
          var avg = frameMs.reduce(function (s, x) { return s + x; }, 0) / frameMs.length; frameMs = [];
          if (avg > 24 && scale > 0.32) scale = Math.max(0.32, scale * 0.82);
          else if (avg < 13 && scale < 0.75) scale = Math.min(0.75, scale * 1.08);
        }
      }
      lastT = now;
    }

    if (canvas && init()) {
      root.classList.add('has-gl');
      started = performance.now();
      on(canvas, 'webglcontextlost', function (e) { e.preventDefault(); root.classList.remove('has-gl'); alive = false; cancelAnimationFrame(raf); });
      on(window, 'pointermove', function (e) { tp.x = (e.clientX / window.innerWidth - 0.5) * 2; tp.y = (e.clientY / window.innerHeight - 0.5) * 2; }, { passive: true });
      on(document, 'pointerout', function (e) { if (!e.relatedTarget) tp.x = tp.y = 0; });
      on(auth, 'input', function (e) { if (e.target && e.target.matches && e.target.matches('input')) glowKick = Math.max(glowKick, 0.9); });
      on(auth, 'focusin', function (e) { focused = !!(e.target && e.target.matches && e.target.matches('input')); });
      on(auth, 'focusout', function () { focused = false; });
      if (reduce) { draw(performance.now()); on(window, 'resize', function () { draw(performance.now()); }); }
      else raf = requestAnimationFrame(frame);
    }

    return {
      root: root, auth: auth,
      destroy: function () { alive = false; cancelAnimationFrame(raf); cleanups.forEach(function (f) { f(); }); }
    };
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
  if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', boot);
  else document.addEventListener('DOMContentLoaded', function () { if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', boot); });
})();
