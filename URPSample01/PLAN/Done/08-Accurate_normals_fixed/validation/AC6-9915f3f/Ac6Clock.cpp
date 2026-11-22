#include "IUnityInterface.h"
#include <d3d11.h>
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include <atomic>
static ID3D11Device *d; static ID3D11DeviceContext *c;
static ID3D11Query *dis,*start,*stop;
static std::atomic<long long> result{-1};
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* i){
 auto u=i->Get<IUnityGraphicsD3D11>(); if(!u || !(d=u->GetDevice()))return;
 d->GetImmediateContext(&c); D3D11_QUERY_DESC q={D3D11_QUERY_TIMESTAMP_DISJOINT,0}; d->CreateQuery(&q,&dis);
 q.Query=D3D11_QUERY_TIMESTAMP; d->CreateQuery(&q,&start); d->CreateQuery(&q,&stop);
}
static void UNITY_INTERFACE_API eventFn(int id){
 if(!dis || !start || !stop){result=-2;return;}
 if(id==1){c->Begin(dis);c->End(start);return;}
 c->End(stop);c->End(dis);c->Flush(); D3D11_QUERY_DATA_TIMESTAMP_DISJOINT f={};UINT64 a=0,b=0;
 HRESULT h; auto deadline=GetTickCount64()+5000;
 while((h=c->GetData(dis,&f,sizeof(f),0))==S_FALSE && GetTickCount64()<deadline) Sleep(0);
 result=(h==S_OK && !f.Disjoint && f.Frequency && c->GetData(start,&a,sizeof(a),0)==S_OK && c->GetData(stop,&b,sizeof(b),0)==S_OK && b>a)? (long long)((b-a)*1e9/f.Frequency):-3;
}
extern "C" __declspec(dllexport) void* callback(){result=-1;return (void*)eventFn;}
extern "C" __declspec(dllexport) long long nanos(){return result.load();}
